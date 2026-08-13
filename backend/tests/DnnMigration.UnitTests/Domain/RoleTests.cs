using System.Reflection;
using DnnMigration.Domain.Abstractions.Services;
using DnnMigration.Domain.Common;
using DnnMigration.Domain.Entities;
using DnnMigration.Domain.Enums;
using FluentAssertions;
using Moq;
using Xunit;

namespace DnnMigration.UnitTests.Domain;

/// <summary>
/// Covers the role aggregate, the group it may belong to, the assignment that joins it to an account, and
/// the two enumerations they use - one whose members are stored characters and one whose members are
/// ordinals.
/// </summary>
/// <remarks>
/// <para>
/// <c>dbo.Roles.RoleID</c> and <c>dbo.RoleGroups.RoleGroupID</c> are both declared <c>IDENTITY(0, 1)</c>,
/// so zero is a real key for both - and in a freshly provisioned installation zero is the
/// <em>Administrators</em> role, the most privileged one there is.
/// </para>
/// <para>
/// <see cref="BillingFrequency"/> is the only enumeration in the domain whose members are not ordinals.
/// <c>dbo.Roles.BillingFrequency</c> is <c>char(1)</c> and the stored letters are load-bearing data, so the
/// members carry those letters as their values and the persistence layer converts by casting rather than by
/// mapping a table.
/// </para>
/// </remarks>
public class RoleTests
{
    /// <summary>
    /// The first value <c>dbo.Roles.RoleID</c> can take, from its <c>IDENTITY(0, 1)</c> declaration at
    /// <c>01.00.00.SqlDataProvider</c> line 115. In a fresh installation this is the
    /// <em>Administrators</em> role, inserted under <c>SET IDENTITY_INSERT</c> at line 7192.
    /// </summary>
    private const int RoleIdentitySeed = 0;

    /// <summary>
    /// The first value <c>dbo.RoleGroups.RoleGroupID</c> can take, from its <c>IDENTITY(0, 1)</c>
    /// declaration at <c>03.02.03.SqlDataProvider</c> line 18.
    /// </summary>
    private const int RoleGroupIdentitySeed = 0;

    /// <summary>
    /// The first value <c>dbo.UserRoles.UserRoleID</c> can take, from its <c>IDENTITY(1, 1)</c> declaration
    /// at <c>01.00.00.SqlDataProvider</c> line 239. Deliberately not zero, which is the whole point of
    /// comparing it with the two constants above.
    /// </summary>
    private const int UserRoleIdentitySeed = 1;

    /// <summary>
    /// The legacy <c>glbRoleAllUsers</c> constant from <c>Library/Components/Shared/Globals.vb</c> line 95,
    /// which grants a permission to every visitor. It collides exactly with <c>Null.NullInteger</c>, and
    /// the collision is why it is named here rather than written inline.
    /// </summary>
    private const int RoleIdAllUsers = -1;

    /// <summary>
    /// A fixed instant for the temporal assertions, so that no test result can depend on when it runs.
    /// </summary>
    private static readonly DateTime FixedNow = new(2024, 6, 15, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// Every member of <see cref="BillingFrequency"/> paired with the character
    /// <c>dbo.Roles.BillingFrequency</c> stores for it. Used to prove the set is exactly this size and
    /// contains exactly these pairs.
    /// </summary>
    public static TheoryData<BillingFrequency, char> FrequencyCodes =>
        new()
        {
            { BillingFrequency.None, 'N' },
            { BillingFrequency.OneTime, 'O' },
            { BillingFrequency.Day, 'D' },
            { BillingFrequency.Week, 'W' },
            { BillingFrequency.Month, 'M' },
            { BillingFrequency.Year, 'Y' },
        };

    // =================================================================================
    // BillingFrequency - the enumeration whose values are persisted characters.
    // =================================================================================

    /// <summary>
    /// Each member's value is the character the column stores for it, and the conversion is reversible.
    /// </summary>
    /// <param name="frequency">The member under test.</param>
    /// <param name="expected">The character the column holds for it.</param>
    [Theory]
    [MemberData(nameof(FrequencyCodes))]
    public void BillingFrequency_MembersCarryTheStoredCharacter(BillingFrequency frequency, char expected)
    {
        ((char)(ushort)frequency).Should().Be(
            expected,
            "the persistence layer converts by casting the member to a character, so the member value "
            + "and the stored character are the same thing");

        ((BillingFrequency)expected).Should().Be(frequency, "and the conversion is reversible");
    }

    /// <summary>The enumeration has exactly six members, matching the six codes the legacy code recognised.</summary>
    [Fact]
    public void BillingFrequency_HasExactlySixMembers()
    {
        BillingFrequency[] members = Enum.GetValues<BillingFrequency>();

        members.Should().HaveCount(6);
        members.Should().OnlyHaveUniqueItems("two members sharing a value would make the cast ambiguous");

        Enum.GetNames<BillingFrequency>().Should().BeEquivalentTo(
            ["None", "OneTime", "Day", "Week", "Month", "Year"]);
    }

    /// <summary>
    /// The set of members is exactly the set of documented codes, with nothing added and nothing missing.
    /// </summary>
    [Fact]
    public void BillingFrequency_CoversEveryDocumentedCodeAndNoOther()
    {
        char[] declared = Enum.GetValues<BillingFrequency>()
            .Select(frequency => (char)(ushort)frequency)
            .ToArray();

        declared.Should().BeEquivalentTo(['N', 'O', 'D', 'W', 'M', 'Y']);

        Enum.IsDefined((BillingFrequency)'A').Should().BeFalse();
        Enum.IsDefined((BillingFrequency)'n').Should().BeFalse(
            "the legacy library compared strings binary, so the lower-case letter was never a code "
            + "either");
    }

    /// <summary>Zero is not a billing frequency, so the type must never be used in a non-nullable position.</summary>
    [Fact]
    public void BillingFrequency_HasNoZeroMember()
    {
        Enum.IsDefined(default(BillingFrequency)).Should().BeFalse(
            "no stored character has the code point zero, so a non-nullable field of this type left at "
            + "its CLR default would hold a value the schema cannot represent - which is why every "
            + "frequency column and property is nullable");

        Enum.IsDefined(BillingFrequency.None).Should().BeTrue();
    }

    /// <summary>One enumeration serves both the billing frequency and the trial frequency.</summary>
    [Fact]
    public void BillingFrequency_TypesBothTheBillingAndTheTrialFrequency()
    {
        // Not a stylistic economy: the two columns share a vocabulary in the data.
        Role role = new()
        {
            RoleId = 1,
            RoleName = "Subscribers",
            BillingFrequency = BillingFrequency.Month,
            TrialFrequency = BillingFrequency.Week,
        };

        role.BillingFrequency.Should().Be(BillingFrequency.Month);
        role.TrialFrequency.Should().Be(BillingFrequency.Week);

        typeof(Role).GetProperty(nameof(Role.TrialFrequency))!.PropertyType
            .Should().Be(
                typeof(Role).GetProperty(nameof(Role.BillingFrequency))!.PropertyType,
                "both properties are the same nullable enumeration type");
    }

    /// <summary>Both frequencies accept every member, so neither column is restricted to a subset.</summary>
    /// <param name="frequency">The member under test.</param>
    /// <param name="expected">The character the column holds for it, unused beyond arity.</param>
    [Theory]
    [MemberData(nameof(FrequencyCodes))]
    public void BillingFrequency_EveryMemberIsAcceptedByBothProperties(
        BillingFrequency frequency,
        char expected)
    {
        Role role = new()
        {
            RoleId = 1,
            RoleName = "Subscribers",
            BillingFrequency = frequency,
            TrialFrequency = frequency,
        };

        role.BillingFrequency.Should().Be(frequency);
        role.TrialFrequency.Should().Be(frequency);
        ((char)(ushort)role.TrialFrequency!.Value).Should().Be(expected);
    }

    /// <summary>The absence of a frequency and the 'N' frequency are different states.</summary>
    [Fact]
    public void BillingFrequency_AbsenceIsNotTheNoneMember()
    {
        Role free = new() { RoleId = 1, RoleName = "Free" };
        Role explicitlyNone = new()
        {
            RoleId = 2,
            RoleName = "Charged Nothing",
            BillingFrequency = BillingFrequency.None,
        };

        free.BillingFrequency.Should().BeNull();
        explicitlyNone.BillingFrequency.Should().Be(BillingFrequency.None);
        free.BillingFrequency.Should().NotBe(explicitlyNone.BillingFrequency);
    }

    /// <summary>
    /// Records the three facts about the legacy frequency switch that this file must not silently absorb,
    /// and pins the far-future date the 'O' code produced.
    /// </summary>
    [Fact]
    public void BillingFrequency_LegacySwitchSemanticsAreRecordedNotReinterpreted()
    {
        // The legacy switch at RoleController.vb lines 540-547 has NO Case Else. An unrecognised code
        // therefore left ExpiryDate at whatever value it arrived with, silently.
        Enum.GetValues<BillingFrequency>().Should().HaveCount(6);

        // The 'O' arm produced New System.DateTime(9999, 12, 31) - MIDNIGHT on that date, not
        // DateTime.MaxValue, which is 9999-12-31T23:59:59.9999999.
        DateTime oneTimeExpiry = new(9999, 12, 31, 0, 0, 0, DateTimeKind.Utc);

        oneTimeExpiry.TimeOfDay.Should().Be(TimeSpan.Zero, "the legacy expression was midnight");
        oneTimeExpiry.Should().NotBe(DateTime.MaxValue);
        DateTime.MaxValue.TimeOfDay.Should().NotBe(TimeSpan.Zero, "which is what makes them different");

        ((char)(ushort)BillingFrequency.Month).Should().Be('M', "which is what '4' was migrated to");
        ((char)(ushort)BillingFrequency.None).Should().Be('N', "which is what '0' was migrated to");

        // MIGRATION: RoleController.vb line 25 imports Microsoft.VisualBasic - the single in-scope use of
        // the VB runtime - purely for DateAdd. The import is removed and the four arithmetic arms become
        // AddDays, AddDays(period * 7), AddMonths and AddYears.
        typeof(BillingFrequency).Assembly.Should().BeSameAs(
            typeof(Role).Assembly,
            "the enumeration is a domain type and pulls in no runtime library of any kind");
    }

    // =================================================================================
    // RoleStatus - the ordinal enumeration, included for the contrast.
    // =================================================================================

    /// <summary>The membership status enumeration is ordinal, unlike the billing frequency.</summary>
    [Fact]
    public void RoleStatus_IsOrdinal()
    {
        ((int)RoleStatus.Pending).Should().Be(0);
        ((int)RoleStatus.Active).Should().Be(1);
        ((int)RoleStatus.Expired).Should().Be(2);

        Enum.GetValues<RoleStatus>().Should().HaveCount(3);
        Enum.GetNames<RoleStatus>().Should().BeEquivalentTo(["Pending", "Active", "Expired"]);
    }

    /// <summary>The default of the status enumeration is a defined member, unlike the billing frequency.</summary>
    [Fact]
    public void RoleStatus_DefaultIsTheLowestMember()
    {
        Enum.IsDefined(default(RoleStatus)).Should().BeTrue();
        default(RoleStatus).Should().Be(RoleStatus.Pending);

        Enum.GetValues<RoleStatus>().Min().Should().Be(RoleStatus.Pending);

        // And the contrast that matters, stated as an assertion rather than a comment.
        Enum.IsDefined(default(BillingFrequency)).Should().BeFalse(
            "a character-valued enumeration has no zero member, so the two types must be treated "
            + "differently at every non-nullable position");
    }

    // =================================================================================
    // Role - identity, sentinels and the paid-membership terms.
    // =================================================================================

    /// <summary>The role reports its primary key as its identity, zero seed included.</summary>
    /// <param name="roleId">The identifier under test.</param>
    [Theory]
    [InlineData(RoleIdentitySeed)]
    [InlineData(RoleIdAllUsers)]
    [InlineData(1)]
    [InlineData(11)]
    [InlineData(int.MaxValue)]
    public void Identity_IsThePrimaryKey(int roleId)
    {
        Role role = new() { RoleId = roleId, RoleName = "Administrators" };
        Role sameRow = new() { RoleId = roleId, RoleName = "A Different Name" };

        // Identity-based comparison applies only once the persistence layer has declared the identity real.
        role.MarkIdentityPersisted();
        sameRow.MarkIdentityPersisted();

        role.Identity.Should().Be(roleId);
        role.Should().Be(sameRow);
    }

    /// <summary>
    /// The zero key belongs to the administrator role of a fresh installation and is not an absence.
    /// </summary>
    [Fact]
    public void Identity_TreatsZeroAsTheAdministratorRoleRatherThanAnAbsence()
    {
        // and the assignment table carries zero as a live foreign key:
        Role administrators = new()
        {
            RoleId = RoleIdentitySeed,
            PortalId = 0,
            RoleName = "Administrators",
            Description = "Portal Administration",
        };
        Role registered = new() { RoleId = 11, PortalId = 0, RoleName = "Registered Users" };

        administrators.MarkIdentityPersisted();
        registered.MarkIdentityPersisted();

        administrators.Identity.Should().Be(0);
        administrators.RoleId.Should().Be(0, "zero is a key, not a placeholder");
        administrators.Should().NotBe(registered);
    }

    /// <summary>Minus one is a role that grants to every visitor, not a role that is missing.</summary>
    [Fact]
    public void Identity_TreatsMinusOneAsTheAllUsersRoleRatherThanAnAbsence()
    {
        // Public Const glbRoleAllUsers As String = "-1"
        // This is the sharpest sentinel collision in the whole migration, and it is security-relevant
        // rather than merely awkward.
        Role allUsers = new() { RoleId = RoleIdAllUsers, RoleName = "All Users" };
        Role alsoAllUsers = new() { RoleId = -1, RoleName = "All Users" };
        Role administrators = new() { RoleId = RoleIdentitySeed, RoleName = "Administrators" };

        allUsers.MarkIdentityPersisted();
        alsoAllUsers.MarkIdentityPersisted();
        administrators.MarkIdentityPersisted();

        allUsers.RoleId.Should().Be(-1, "the value survives assignment unchanged");
        allUsers.Identity.Should().Be(-1, "and reaches equality unchanged");
        allUsers.Should().Be(alsoAllUsers, "so two references to the all-users role are one entity");
        allUsers.Should().NotBe(administrators, "and it is a different role from the zero-keyed one");

        // The property is a plain non-nullable int: there is no nullable projection through which a
        // -1 could become a null in the first place.
        typeof(Role).GetProperty(nameof(Role.RoleId))!.PropertyType.Should().Be(typeof(int));
    }

    /// <summary>
    /// A role may belong to the installation rather than to a tenant, and the tenant it belongs to may
    /// legitimately be numbered minus one.
    /// </summary>
    [Fact]
    public void Role_PortalOwnershipIsOptional()
    {
        Role hostRole = new() { RoleId = 1, RoleName = "Superusers" };
        Role firstPortalRole = new() { RoleId = 2, PortalId = -1, RoleName = "Registered Users" };
        Role secondPortalRole = new() { RoleId = 3, PortalId = 0, RoleName = "Registered Users" };

        hostRole.PortalId.Should().BeNull("null is how the domain says host scope");
        firstPortalRole.PortalId.Should().Be(
            -1,
            "minus one is the first tenant of an installation, not an absent tenant - see PortalTests");
        secondPortalRole.PortalId.Should().Be(0);

        typeof(Role).GetProperty(nameof(Role.PortalId))!.PropertyType.Should().Be(typeof(int?));
    }

    /// <summary>A role that belongs to no group says so with a null rather than with minus one.</summary>
    [Fact]
    public void Role_GroupMembershipIsOptionalAndAbsenceIsNull()
    {
        // which was forced on it because RoleInfo.RoleGroupID was a non-nullable VB Integer while the
        // column it maps to is nullable - added as "ALTER TABLE Roles ADD RoleGroupID int NULL" at
        // 03.02.03.SqlDataProvider line 34 with FK_Roles_RoleGroups following at line 37.
        Role ungrouped = new() { RoleId = 1, RoleName = "Administrators" };
        Role grouped = new() { RoleId = 2, RoleName = "Subscribers", RoleGroupId = RoleGroupIdentitySeed };

        ungrouped.RoleGroupId.Should().BeNull("absence is null, not minus one");
        grouped.RoleGroupId.Should().Be(0, "and zero is a real group, so it is not absence either");

        typeof(Role).GetProperty(nameof(Role.RoleGroupId))!.PropertyType.Should().Be(typeof(int?));
    }

    /// <summary>An empty string stays an empty string and is never quietly turned into a null.</summary>
    /// <param name="value">The text under test.</param>
    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("Portal Administration")]
    public void Role_TextValuesRoundTripWithoutCoercion(string value)
    {
        // MIGRATION: the legacy null marker for text was the EMPTY STRING, not null - Null.vb declares
        // NullString as "" - so the legacy code could not distinguish a SQL NULL from an empty string once
        // a row had been read.
        Role role = new()
        {
            RoleId = 1,
            RoleName = value,
            Description = value,
            RsvpCode = value,
            IconFile = value,
        };

        role.RoleName.Should().Be(value);
        role.Description.Should().Be(value);
        role.RsvpCode.Should().Be(value);
        role.IconFile.Should().Be(value);
    }

    /// <summary>
    /// The three nullable text members distinguish a null from an empty string; the mandatory name does not
    /// offer the choice at all.
    /// </summary>
    [Fact]
    public void Role_NullAndEmptyTextAreDifferentStates()
    {
        Role blank = new()
        {
            RoleId = 1,
            RoleName = string.Empty,
            Description = string.Empty,
            RsvpCode = string.Empty,
            IconFile = string.Empty,
        };
        Role absent = new() { RoleId = 2, RoleName = "Administrators" };

        blank.Description.Should().BeEmpty().And.NotBeNull();
        blank.RsvpCode.Should().BeEmpty().And.NotBeNull();
        blank.IconFile.Should().BeEmpty().And.NotBeNull();

        absent.Description.Should().BeNull();
        absent.RsvpCode.Should().BeNull();
        absent.IconFile.Should().BeNull();

        // RoleName maps to "nvarchar(50) NOT NULL" (01.00.00 line 117), so it is non-nullable and defaults
        // to the empty string rather than to null.
        typeof(Role).GetProperty(nameof(Role.RoleName))!.PropertyType.Should().Be(typeof(string));
        new Role { RoleId = 3 }.RoleName.Should().BeEmpty("the name column is not nullable");
    }

    /// <summary>All six paid-membership terms are present on the entity.</summary>
    [Fact]
    public void Role_CarriesAllSixPaidMembershipTerms()
    {
        Role role = new()
        {
            RoleId = 1,
            RoleName = "Subscribers",
            ServiceFee = 9.99m,
            BillingPeriod = 1,
            BillingFrequency = BillingFrequency.Month,
            TrialFee = 0m,
            TrialPeriod = 14,
            TrialFrequency = BillingFrequency.Day,
        };

        role.ServiceFee.Should().Be(9.99m);
        role.BillingPeriod.Should().Be(1);
        role.BillingFrequency.Should().Be(BillingFrequency.Month);
        role.TrialFee.Should().Be(0m);
        role.TrialPeriod.Should().Be(14);
        role.TrialFrequency.Should().Be(BillingFrequency.Day);
    }

    /// <summary>A newly constructed role carries no paid-membership terms at all.</summary>
    [Fact]
    public void Role_CarriesNoPaidMembershipTermsByDefault()
    {
        // RoleInfo.vb declares NO constructor at all - not an empty one, none - so every legacy field took
        // its CLR default: 0 for the integers, Nothing for the strings, False for the booleans.
        Role role = new() { RoleId = 1, RoleName = "Subscribers" };

        role.ServiceFee.Should().BeNull();
        role.BillingPeriod.Should().BeNull();
        role.BillingFrequency.Should().BeNull(
            "a role with no billing frequency is free, which is a different state from a role billed "
            + "under the 'N' frequency");
        role.TrialFee.Should().BeNull();
        role.TrialPeriod.Should().BeNull();
        role.TrialFrequency.Should().BeNull();
        role.RoleGroupId.Should().BeNull();
        role.RsvpCode.Should().BeNull();
        role.IconFile.Should().BeNull();
        role.Description.Should().BeNull();
    }

    /// <summary>
    /// The fee members store exactly what they are given, including a negative, because clamping is not the
    /// entity's job.
    /// </summary>
    /// <param name="fee">The fee under test.</param>
    [Theory]
    [InlineData(-1)]
    [InlineData(-0.01)]
    [InlineData(0)]
    [InlineData(0.01)]
    [InlineData(999.99)]
    public void Role_FeesAreStoredVerbatimAndAreNotClampedByTheEntity(double fee)
    {
        decimal value = (decimal)fee;

        Role role = new()
        {
            RoleId = 1,
            RoleName = "Subscribers",
            ServiceFee = value,
            TrialFee = value,
        };

        role.ServiceFee.Should().Be(value, "the entity is a faithful store, not a validator");
        role.TrialFee.Should().Be(value);
    }

    /// <summary>The fee members are decimals, which is a deliberate correction of a legacy type mismatch.</summary>
    [Fact]
    public void Role_FeesAreDecimalRatherThanSingle()
    {
        // The legacy properties were VB Single, and the target maps them to decimal.
        typeof(Role).GetProperty(nameof(Role.ServiceFee))!.PropertyType.Should().Be(typeof(decimal?));
        typeof(Role).GetProperty(nameof(Role.TrialFee))!.PropertyType.Should().Be(typeof(decimal?));

        Role role = new() { RoleId = 1, RoleName = "Subscribers", ServiceFee = 9.99m, TrialFee = 0.07m };

        role.ServiceFee.Should().Be(9.99m, "a decimal represents an exact currency amount exactly");
        role.TrialFee.Should().Be(0.07m, "which a binary float does not");

        Role fourPlaces = new() { RoleId = 2, RoleName = "Metered", ServiceFee = 0.0001m };

        fourPlaces.ServiceFee.Should().Be(0.0001m, "money keeps four decimal places");

        // And an amount well beyond the baseline's three-integer-digit ceiling is equally unremarkable
        // for money.
        Role large = new() { RoleId = 3, RoleName = "Enterprise", ServiceFee = 1_000_000.50m };

        large.ServiceFee.Should().Be(
            1_000_000.50m,
            "the terminal column is money, so 999.99 is not a limit - that was the baseline's");
    }

    /// <summary>A newly constructed role is neither public nor automatically assigned.</summary>
    [Fact]
    public void Role_IsNeitherPublicNorAutomaticByDefault()
    {
        Role role = new() { RoleId = 1, RoleName = "Administrators" };

        role.IsPublic.Should().BeFalse();
        role.AutoAssignment.Should().BeFalse();
        role.RoleName.Should().Be("Administrators");
    }

    /// <summary>
    /// Two roles with different keys are different entities, and a role never equals another kind of entity
    /// that happens to share its key.
    /// </summary>
    [Fact]
    public void Identity_DistinguishesDifferentKeysAndDifferentTypes()
    {
        Role administrators = new() { RoleId = 0, RoleName = "Administrators" };
        Role registered = new() { RoleId = 11, RoleName = "Registered Users" };

        administrators.MarkIdentityPersisted();
        registered.MarkIdentityPersisted();

        administrators.Should().NotBe(registered);
        administrators.GetHashCode().Should().NotBe(
            registered.GetHashCode(),
            "distinct keys of the same type should land in distinct buckets");
    }

    /// <summary>Identity comparison waits for the persistence layer to declare the key real.</summary>
    [Fact]
    public void Identity_IsNotComparedBeforeThePersistenceLayerDeclaresIt()
    {
        // This is the invariant that makes the zero seed survivable, and it is the reason there is
        // deliberately NO IsTransient, IsNew or "identity == default" test anywhere in this file.
        Role first = new() { RoleId = RoleIdentitySeed, RoleName = "Administrators" };
        Role second = new() { RoleId = RoleIdentitySeed, RoleName = "Administrators" };

        first.IdentityIsPersisted.Should().BeFalse("a constructed entity has not been declared");
        first.Should().NotBe(
            second,
            "otherwise every freshly constructed role would equal every other, since they all start "
            + "at the zero seed");
        first.Should().Be(first, "though an instance is always itself");

        first.MarkIdentityPersisted();
        first.Should().NotBe(second, "one-sided declaration is not enough");

        second.MarkIdentityPersisted();
        first.Should().Be(second, "once both are declared, the shared key makes them one entity");
    }

    /// <summary>The entity carries no serialisation or mapping attribute of any kind.</summary>
    [Fact]
    public void Role_CarriesNoAttributes()
    {
        foreach (Type entityType in new[] { typeof(Role), typeof(RoleGroup), typeof(UserRole) })
        {
            AuthorApplied(entityType.GetCustomAttributes(inherit: false)).Should().BeEmpty(
                "{0} must carry no attribute of its own",
                entityType.Name);

            foreach (PropertyInfo property in entityType.GetProperties())
            {
                AuthorApplied(property.GetCustomAttributes(inherit: false)).Should().BeEmpty(
                    "{0}.{1} must carry no attribute",
                    entityType.Name,
                    property.Name);
            }
        }
    }

    /// <summary>
    /// Filters a metadata attribute set down to the attributes an author actually wrote, discarding the
    /// ones the compiler emits to record nullable annotations.
    /// </summary>
    /// <param name="attributes">The raw attribute set read from metadata.</param>
    /// <returns>Only the attributes declared in source.</returns>
    private static IEnumerable<string> AuthorApplied(object[] attributes) =>
        attributes
            .Select(attribute => attribute.GetType())
            .Where(type => type.Namespace != "System.Runtime.CompilerServices")
            .Select(type => type.FullName ?? type.Name);

    // =================================================================================
    // RoleGroup - the fifth zero-seeded identity, and the one easiest to miss.
    // =================================================================================

    /// <summary>The group reports its primary key as its identity, and zero is a real group.</summary>
    [Fact]
    public void GroupIdentity_IsThePrimaryKeyAndZeroIsARealGroup()
    {
        // Dbo.RoleGroups.RoleGroupID is declared IDENTITY(0, 1), so zero is a real key here exactly as it
        // is for dbo.Roles. This is the FIFTH zero-or-negative identity seed in scope, and it is the one
        // that hides, so it is worth recording how it hides.
        RoleGroup group = new() { RoleGroupId = RoleGroupIdentitySeed, PortalId = 0, RoleGroupName = "Global" };
        RoleGroup sameRow = new() { RoleGroupId = 0, PortalId = 9, RoleGroupName = "Other" };
        RoleGroup otherRow = new() { RoleGroupId = 1, PortalId = 0, RoleGroupName = "Global" };

        group.MarkIdentityPersisted();
        sameRow.MarkIdentityPersisted();
        otherRow.MarkIdentityPersisted();

        group.Identity.Should().Be(0);
        group.RoleGroupId.Should().Be(0, "zero is a key, not a placeholder");
        group.Should().Be(sameRow, "the key alone decides identity");
        group.Should().NotBe(otherRow);
    }

    /// <summary>The group carries exactly the four columns of its table.</summary>
    [Fact]
    public void Group_CarriesItsFourColumns()
    {
        // [RoleGroupID] int IDENTITY(0,1) NOT NULL [PortalID] int NOT NULL [RoleGroupName] nvarchar(50) NOT
        // NULL [Description] nvarchar(1000) NULL.
        RoleGroup group = new()
        {
            RoleGroupId = 3,
            PortalId = 0,
            RoleGroupName = "Subscription Groups",
            Description = "Groups that carry a fee",
        };

        group.RoleGroupId.Should().Be(3);
        group.PortalId.Should().Be(0);
        group.RoleGroupName.Should().Be("Subscription Groups");
        group.Description.Should().Be("Groups that carry a fee");

        typeof(RoleGroup).GetProperty(nameof(RoleGroup.PortalId))!.PropertyType
            .Should().Be(typeof(int), "the column is NOT NULL, so the property is not nullable");
        typeof(RoleGroup).GetProperty(nameof(RoleGroup.RoleGroupName))!.PropertyType
            .Should().Be(typeof(string), "the column is NOT NULL");

        new RoleGroup { RoleGroupId = 4, PortalId = 0 }.RoleGroupName
            .Should().BeEmpty("a mandatory name defaults to the empty string rather than to null");
        new RoleGroup { RoleGroupId = 5, PortalId = 0 }.Description
            .Should().BeNull("the description column is nullable");
    }

    /// <summary>
    /// A group always belongs to a tenant, and that tenant may legitimately be numbered minus one or zero.
    /// </summary>
    [Fact]
    public void Group_AlwaysBelongsToATenant()
    {
        // dbo.RoleGroups.PortalID is "int NOT NULL" with a foreign key to dbo.Portals declared ON DELETE
        // CASCADE, so a group cannot outlive its portal and cannot be host-scoped at all which is a real
        // difference from dbo.Roles.PortalID, where NULL means host scope.
        RoleGroup firstPortal = new() { RoleGroupId = 1, PortalId = -1, RoleGroupName = "Global" };
        RoleGroup secondPortal = new() { RoleGroupId = 2, PortalId = 0, RoleGroupName = "Global" };

        firstPortal.PortalId.Should().Be(-1, "the first tenant of an installation is numbered -1");
        secondPortal.PortalId.Should().Be(0);
    }

    /// <summary>A group name is unique within a tenant but free to repeat across tenants.</summary>
    [Fact]
    public void Group_NamesAreScopedToTheirTenant()
    {
        // The constraint is declared at 03.02.03.SqlDataProvider line 28:
        RoleGroup inFirstPortal = new() { RoleGroupId = 1, PortalId = -1, RoleGroupName = "Global" };
        RoleGroup inSecondPortal = new() { RoleGroupId = 2, PortalId = 0, RoleGroupName = "Global" };

        inFirstPortal.MarkIdentityPersisted();
        inSecondPortal.MarkIdentityPersisted();

        inFirstPortal.RoleGroupName.Should().Be(inSecondPortal.RoleGroupName, "the names may repeat");
        inFirstPortal.PortalId.Should().NotBe(inSecondPortal.PortalId, "in different tenants");
        inFirstPortal.Should().NotBe(inSecondPortal, "and they remain two distinct groups");
    }

    /// <summary>A role and a group carrying the same numeric key are different entities.</summary>
    [Fact]
    public void RoleAndGroup_ShareTheZeroSeedButNotIdentity()
    {
        // Both tables seed at zero, so without an exact runtime-type comparison the zero-keyed role
        // and the zero-keyed group would be indistinguishable to equality.
        Role role = new() { RoleId = 0, RoleName = "Administrators" };
        RoleGroup group = new() { RoleGroupId = 0, PortalId = 0, RoleGroupName = "Global" };

        role.MarkIdentityPersisted();
        group.MarkIdentityPersisted();

        role.Identity.Should().Be(group.Identity, "the keys really are equal");
        ((Entity<int>)role).Equals(group).Should().BeFalse("but the entities are not");
        role.GetHashCode().Should().NotBe(group.GetHashCode(), "and they hash apart");
    }

    /// <summary>A newly constructed role and group expose empty collections rather than null ones.</summary>
    [Fact]
    public void NavigationCollections_AreInitialisedRatherThanNull()
    {
        Role role = new() { RoleId = 1, RoleName = "Administrators" };
        RoleGroup group = new() { RoleGroupId = 1, PortalId = 0, RoleGroupName = "Global" };

        role.UserRoles.Should().NotBeNull().And.BeEmpty();
        role.ModulePermissions.Should().NotBeNull().And.BeEmpty();
        role.TabPermissions.Should().NotBeNull().And.BeEmpty();
        group.Roles.Should().NotBeNull().And.BeEmpty();
    }

    /// <summary>Attaching a role to a group through the navigation leaves both sides consistent.</summary>
    [Fact]
    public void Group_CanCarryItsRolesThroughTheNavigation()
    {
        RoleGroup group = new() { RoleGroupId = 0, PortalId = 0, RoleGroupName = "Global" };
        Role role = new() { RoleId = 0, PortalId = 0, RoleName = "Administrators", RoleGroup = group };

        group.Roles.Add(role);

        group.Roles.Should().ContainSingle().Which.Should().BeSameAs(role);
        role.RoleGroup.Should().BeSameAs(group);
        role.RoleGroupId.Should().BeNull(
            "the foreign key stays unset until the persistence layer fixes it up from the navigation. "
            + "That is why the write path assigns the navigation and never the key: RoleGroupID is "
            + "IDENTITY(0, 1), so a key assigned before the insert could legitimately be zero and would "
            + "be indistinguishable from one that had never been set");
    }

    // =================================================================================
    // UserRole - the assignment, and the inheritance that is no longer there.
    // =================================================================================

    /// <summary>The assignment is a sibling of the role, not a subclass of it.</summary>
    [Fact]
    public void Assignment_DoesNotInheritTheRoleDefinition()
    {
        // Public Class UserRoleInfo Inherits RoleInfo
        // so all fifteen of the role definition's own members - PortalID, RoleName, ServiceFee,
        // BillingFrequency and the rest - arrived on the assignment object as if the assignment owned them.
        // It does not.
        typeof(UserRole).BaseType.Should().Be(
            typeof(Entity<int>),
            "the assignment derives from the identity base directly, not from the role");
        typeof(Role).IsAssignableFrom(typeof(UserRole)).Should().BeFalse(
            "no assignment is a role definition");
        typeof(UserRole).IsSealed.Should().BeTrue("so the hierarchy cannot be reintroduced");
        typeof(Role).IsSealed.Should().BeTrue();

        typeof(UserRole).GetProperty(nameof(UserRole.Role))!.PropertyType.Should().Be(typeof(Role));
    }

    /// <summary>An assignment never equals the role it grants, even when their keys coincide.</summary>
    [Fact]
    public void Assignment_IsNeverEqualToARoleWithTheSameKey()
    {
        // This is the assertion the flattening did not make unnecessary. Equality compares the exact
        // runtime type through GetType() rather than with a type test, which is what keeps an assignment
        // and a role apart while their keys collide.
        const int sharedKey = 5;

        Role role = new() { RoleId = sharedKey, RoleName = "Subscribers" };
        UserRole assignment = new() { UserRoleId = sharedKey, UserId = 9, RoleId = 0 };

        role.MarkIdentityPersisted();
        assignment.MarkIdentityPersisted();

        role.Identity.Should().Be(assignment.Identity, "the keys really are equal");
        ((Entity<int>)assignment).Equals(role).Should().BeFalse("but the entities are not");
        ((Entity<int>)role).Equals(assignment).Should().BeFalse("in either direction");
        assignment.GetHashCode().Should().NotBe(role.GetHashCode(), "and they hash apart");
    }

    /// <summary>The assignment reports its own surrogate key rather than the pair it joins.</summary>
    [Fact]
    public void AssignmentIdentity_IsItsSurrogateKey()
    {
        UserRole assignment = new() { UserRoleId = 11, UserId = 1, RoleId = 0 };
        UserRole otherAssignment = new() { UserRoleId = 12, UserId = 1, RoleId = 0 };

        assignment.MarkIdentityPersisted();
        otherAssignment.MarkIdentityPersisted();

        assignment.Identity.Should().Be(11);
        assignment.Should().NotBe(
            otherAssignment,
            "the surrogate key decides identity, not the user-and-role pair it happens to carry");
    }

    /// <summary>
    /// The sentinel decision is made per column: zero is a real role key but an unreachable assignment key,
    /// and minus one is the reverse.
    /// </summary>
    /// <param name="key">The key under test.</param>
    /// <param name="isRealRoleKey">Whether the value can occur as a <c>RoleID</c>.</param>
    /// <param name="isRealAssignmentKey">Whether the value can occur as a <c>UserRoleID</c>.</param>
    [Theory]
    [InlineData(-1, true, false)]
    [InlineData(0, true, false)]
    [InlineData(1, true, true)]
    public void Identity_SentinelMeaningIsDecidedPerColumnNotGlobally(
        int key,
        bool isRealRoleKey,
        bool isRealAssignmentKey)
    {
        // The single clearest demonstration in this file that there is no global sentinel rule. Measured
        // seeds, both from 01.00.00.SqlDataProvider:
        isRealRoleKey.Should().BeTrue(
            "every value tested here is reachable as a RoleID: IDENTITY(0, 1) makes 0 the first and "
            + "glbRoleAllUsers makes -1 meaningful");

        Role role = new() { RoleId = key, RoleName = "A Role" };
        role.MarkIdentityPersisted();
        role.Identity.Should().Be(key, "so a RoleID is never reinterpreted");

        UserRole assignment = new() { UserRoleId = key, UserId = 9, RoleId = 0 };
        assignment.MarkIdentityPersisted();
        assignment.Identity.Should().Be(
            key,
            "the entity stores a UserRoleID faithfully whatever it is handed - the schema, not the "
            + "entity, is what makes some values unreachable");

        (key >= UserRoleIdentitySeed).Should().Be(
            isRealAssignmentKey,
            "only values at or above the IDENTITY(1, 1) seed can occur as a persisted UserRoleID, "
            + "which is why -1 is a SAFE absent marker for this column and 0 is not a real value");
    }

    /// <summary>The assignment carries exactly the six columns of its table.</summary>
    [Fact]
    public void Assignment_CarriesItsSixColumns()
    {
        UserRole assignment = new()
        {
            UserRoleId = 1,
            UserId = 9,
            RoleId = RoleIdentitySeed,
            EffectiveDate = FixedNow,
            ExpiryDate = FixedNow.AddDays(30),
            IsTrialUsed = true,
        };

        assignment.UserRoleId.Should().Be(1);
        assignment.UserId.Should().Be(9);
        assignment.RoleId.Should().Be(0, "which is the shipped Administrators role");
        assignment.EffectiveDate.Should().Be(FixedNow);
        assignment.ExpiryDate.Should().Be(FixedNow.AddDays(30));
        assignment.IsTrialUsed.Should().BeTrue();
    }

    /// <summary>Three legacy members are deliberately absent, because none of them is a column.</summary>
    [Fact]
    public void Assignment_OmitsTheLegacyMembersThatWereNotColumns()
    {
        // Subscribed is not a column anywhere in the 88-script chain. Where the name appears it is a
        // computed SELECT alias - a correlated existence test that restates whether a row like this one
        // exists.
        typeof(UserRole).GetProperty("FullName").Should().BeNull("a projection of the joined user row");
        typeof(UserRole).GetProperty("Email").Should().BeNull("likewise a projection");
        typeof(UserRole).GetProperty("Subscribed").Should().BeNull("never a column at all");

        // And the six that are real, named so that adding a seventh is a deliberate act.
        typeof(UserRole).GetProperties()
            .Where(property => property.CanWrite)
            .Select(property => property.Name)
            .Should().BeEquivalentTo(
                [
                    nameof(UserRole.UserRoleId),
                    nameof(UserRole.UserId),
                    nameof(UserRole.RoleId),
                    nameof(UserRole.ExpiryDate),
                    nameof(UserRole.IsTrialUsed),
                    nameof(UserRole.EffectiveDate),
                    nameof(UserRole.User),
                    nameof(UserRole.Role),
                ],
                "six mapped columns plus the two navigations that replace the dropped inheritance");
    }

    /// <summary>
    /// A default-constructed assignment names the administrator role, which is why one is never persisted
    /// unexamined.
    /// </summary>
    [Fact]
    public void Assignment_DefaultsToTheAdministratorRoleWhichIsAHazardNotAConvenience()
    {
        UserRole fresh = new();

        fresh.RoleId.Should().Be(
            RoleIdentitySeed,
            "an unassigned RoleId is indistinguishable from a deliberate grant of Administrators");
        fresh.UserId.Should().Be(0, "and the account is equally unnamed");
        fresh.UserRoleId.Should().Be(
            0,
            "whereas a zero UserRoleID is harmless, because IDENTITY(1, 1) means no row can hold it");

        typeof(UserRole).GetProperty(nameof(UserRole.RoleId))!.PropertyType.Should().Be(
            typeof(int),
            "the column is NOT NULL with a cascading foreign key, so the key cannot be made nullable "
            + "to express 'no role chosen'");
    }

    /// <summary>An assignment carries no window and no trial flag until one is supplied.</summary>
    [Fact]
    public void Assignment_CarriesNoWindowByDefault()
    {
        UserRole assignment = new() { UserRoleId = 1, UserId = 1, RoleId = 0 };

        assignment.EffectiveDate.Should().BeNull("an assignment with no effective date is in force at once");
        assignment.ExpiryDate.Should().BeNull("an assignment with no expiry never lapses");
        assignment.IsTrialUsed.Should().BeNull(
            "the trial flag is a tri-state: unknown is not the same as 'the trial has not been used'");
    }

    /// <summary>
    /// An unbounded membership is classified from its nullable bounds alone, with no sentinel recognised
    /// anywhere in the Domain.
    /// </summary>
    [Fact]
    public void Assignment_ClassifiesFromNullableBoundsAlone()
    {
        IClock clock = FixedClock();

        UserRole unbounded = new() { UserRoleId = 1, UserId = 9, RoleId = 0 };

        unbounded.EffectiveDate.Should().BeNull();
        unbounded.ExpiryDate.Should().BeNull();

        unbounded.GetStatus(clock.UtcNow).Should().Be(
            RoleStatus.Active,
            "a membership with neither bound set is in force, which is what the auto-assignment path "
            + "records once its two absent bounds have been normalised at the write boundary");

        // Each bound alone, to prove the classification reads them independently rather than as a pair.
        new UserRole { UserRoleId = 2, UserId = 9, RoleId = 0, EffectiveDate = clock.UtcNow.AddDays(-1) }
            .GetStatus(clock.UtcNow).Should().Be(RoleStatus.Active);
        new UserRole { UserRoleId = 3, UserId = 9, RoleId = 0, ExpiryDate = clock.UtcNow.AddDays(1) }
            .GetStatus(clock.UtcNow).Should().Be(RoleStatus.Active);
    }

    /// <summary>
    /// The legacy empty-date marker is classified as the real bound it literally is, because the Domain
    /// holds no sentinel knowledge and the boundary removes the marker before it can arrive.
    /// </summary>
    /// <param name="hours">Hours to add to the marker, so a stray time component is covered too.</param>
    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(23)]
    public void Assignment_TreatsTheLegacyEmptyDateAsARealBound(int hours)
    {
        IClock clock = FixedClock();

        UserRole marked = new()
        {
            UserRoleId = 1,
            UserId = 9,
            RoleId = 0,
            EffectiveDate = DateTime.MinValue.AddHours(hours),
            ExpiryDate = DateTime.MinValue.AddHours(hours),
        };

        marked.EffectiveDate.Should().Be(
            DateTime.MinValue.AddHours(hours),
            "the entity stores what it is given and rewrites nothing");

        marked.GetStatus(clock.UtcNow).Should().Be(
            RoleStatus.Expired,
            "an expiry bound of 0001-01-01 is long past, and the Domain says so plainly rather than "
            + "consulting a sentinel table to decide that this particular instant means 'no bound'");
    }

    /// <summary>A real window is classified against the supplied instant, with both bounds inclusive.</summary>
    [Fact]
    public void Assignment_ClassifiesARealWindowAgainstTheSuppliedInstant()
    {
        IClock clock = FixedClock();
        DateTime now = clock.UtcNow;

        UserRole active = new()
        {
            UserRoleId = 1,
            UserId = 9,
            RoleId = 0,
            EffectiveDate = now.AddDays(-1),
            ExpiryDate = now.AddDays(1),
        };
        UserRole pending = new()
        {
            UserRoleId = 2,
            UserId = 9,
            RoleId = 0,
            EffectiveDate = now.AddDays(1),
        };
        UserRole expired = new()
        {
            UserRoleId = 3,
            UserId = 9,
            RoleId = 0,
            ExpiryDate = now.AddDays(-1),
        };
        UserRole onBothBounds = new()
        {
            UserRoleId = 4,
            UserId = 9,
            RoleId = 0,
            EffectiveDate = now,
            ExpiryDate = now,
        };

        active.GetStatus(now).Should().Be(RoleStatus.Active);
        pending.GetStatus(now).Should().Be(RoleStatus.Pending);
        expired.GetStatus(now).Should().Be(RoleStatus.Expired);

        onBothBounds.GetStatus(now).Should().Be(
            RoleStatus.Active,
            "both bounds are inclusive, matching the SQL predicate the repository translates");
    }

    /// <summary>Classification is deterministic: the same assignment and the same instant always agree.</summary>
    [Fact]
    public void Assignment_ClassificationIsDeterministic()
    {
        Mock<IClock> clock = new(MockBehavior.Strict);
        clock.SetupGet(instance => instance.UtcNow).Returns(FixedNow);

        UserRole assignment = new()
        {
            UserRoleId = 1,
            UserId = 9,
            RoleId = 0,
            ExpiryDate = FixedNow.AddDays(-1),
            EffectiveDate = FixedNow.AddDays(1),
        };

        RoleStatus first = assignment.GetStatus(clock.Object.UtcNow);
        RoleStatus second = assignment.GetStatus(clock.Object.UtcNow);

        first.Should().Be(second, "the classifier reads no ambient clock and caches nothing");

        // The contradictory window is reachable in real code, not merely in theory: the cancellation path
        // back-dates the expiry by a day to retain the trial-used fact and never touches the effective
        // date.
        first.Should().Be(RoleStatus.Expired, "expiry outranks a start that has not yet arrived");
    }

    /// <summary>
    /// Builds a clock stub pinned to <see cref="FixedNow"/>, so that no assertion in this file reads the
    /// machine clock.
    /// </summary>
    /// <returns>A clock that always reports the same instant.</returns>
    private static IClock FixedClock()
    {
        Mock<IClock> clock = new(MockBehavior.Strict);
        clock.SetupGet(instance => instance.UtcNow).Returns(FixedNow);

        return clock.Object;
    }
}
