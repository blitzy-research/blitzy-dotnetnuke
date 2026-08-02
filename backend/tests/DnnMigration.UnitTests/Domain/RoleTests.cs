using DnnMigration.Domain.Common;
using DnnMigration.Domain.Entities;
using DnnMigration.Domain.Enums;
using FluentAssertions;
using Xunit;

namespace DnnMigration.UnitTests.Domain;

/// <summary>
/// Covers the role aggregate, the group it may belong to, the assignment that joins it to an account,
/// and the billing-frequency enumeration whose members are stored characters rather than ordinals.
/// </summary>
/// <remarks>
/// <para>
/// <c>dbo.Roles.RoleID</c> and <c>dbo.RoleGroups.RoleGroupID</c> are both declared
/// <c>IDENTITY(0, 1)</c>, so zero is a real key for both - and in a freshly provisioned installation
/// zero is the <em>Administrators</em> role, the most privileged one there is. Reading zero as "unset"
/// would therefore not merely lose a row, it would lose the row that decides who can administer the
/// tenant. That is what the identity assertions below defend.
/// </para>
/// <para>
/// <see cref="BillingFrequency"/> is the only enumeration in the domain whose members are not ordinals.
/// <c>dbo.Roles.BillingFrequency</c> is <c>char(1)</c> and the stored letters are load-bearing data, so
/// the members carry those letters as their values and the persistence layer converts by casting rather
/// than by mapping a table. A consequence worth stating explicitly, and asserted below, is that zero is
/// not a member at all: a non-nullable field of this type left at its CLR default holds an undefined
/// value, which is precisely why every such column and property is nullable.
/// </para>
/// </remarks>
public class RoleTests
{
    private const int RoleIdentitySeed = 0;

    private const int RoleGroupIdentitySeed = 0;

    /// <summary>
    /// The role reports its primary key as its identity, zero seed included.
    /// </summary>
    /// <param name="roleId">The identifier under test.</param>
    [Theory]
    [InlineData(RoleIdentitySeed)]
    [InlineData(1)]
    [InlineData(int.MaxValue)]
    public void Identity_IsThePrimaryKey(int roleId)
    {
        Role role = new() { RoleId = roleId, RoleName = "Administrators" };
        Role sameRow = new() { RoleId = roleId, RoleName = "A Different Name" };

        // MIGRATION: identity-based comparison applies only once the persistence layer has declared
        // the identity real. That declaration is what this test stands in for: every candidate
        // "not saved yet" marker in this schema is a genuine key - the portal table seeds at -1 and
        // the role, page and module tables at 0 - so an undeclared instance is compared by object
        // reference instead, and two separately constructed instances are two different entities.
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
        Role administrators = new() { RoleId = RoleIdentitySeed, PortalId = -1, RoleName = "Administrators" };
        Role registered = new() { RoleId = 1, PortalId = -1, RoleName = "Registered Users" };

        administrators.Identity.Should().Be(0);
        administrators.Should().NotBe(registered);
        administrators.PortalId.Should().Be(-1, "the first tenant of an installation is numbered minus one");
    }

    /// <summary>
    /// The group reports its primary key as its identity, zero seed included.
    /// </summary>
    [Fact]
    public void GroupIdentity_IsThePrimaryKeyAndZeroIsARealGroup()
    {
        RoleGroup group = new() { RoleGroupId = RoleGroupIdentitySeed, PortalId = 0, RoleGroupName = "Global" };
        RoleGroup sameRow = new() { RoleGroupId = 0, PortalId = 9, RoleGroupName = "Other" };
        RoleGroup otherRow = new() { RoleGroupId = 1, PortalId = 0, RoleGroupName = "Global" };

        // MIGRATION: identity-based comparison applies only once the persistence layer has declared
        // the identity real. That declaration is what this test stands in for: every candidate
        // "not saved yet" marker in this schema is a genuine key - the portal table seeds at -1 and
        // the role, page and module tables at 0 - so an undeclared instance is compared by object
        // reference instead, and two separately constructed instances are two different entities.
        group.MarkIdentityPersisted();
        sameRow.MarkIdentityPersisted();
        otherRow.MarkIdentityPersisted();

        group.Identity.Should().Be(0);
        group.Should().Be(sameRow);
        group.Should().NotBe(otherRow);
    }

    /// <summary>
    /// A role and a group carrying the same numeric key are different entities.
    /// </summary>
    [Fact]
    public void RoleAndGroup_ShareTheZeroSeedButNotIdentity()
    {
        Entity<int> role = new Role { RoleId = 0, RoleName = "Administrators" };
        Entity<int> group = new RoleGroup { RoleGroupId = 0, PortalId = 0, RoleGroupName = "Global" };

        role.Equals(group).Should().BeFalse();
    }

    /// <summary>
    /// The assignment reports its own surrogate key rather than the pair it joins.
    /// </summary>
    [Fact]
    public void AssignmentIdentity_IsItsSurrogateKey()
    {
        UserRole assignment = new() { UserRoleId = 11, UserId = 1, RoleId = 0 };

        assignment.Identity.Should().Be(11);
        assignment.Should().NotBe(new UserRole { UserRoleId = 12, UserId = 1, RoleId = 0 });
    }

    /// <summary>
    /// A newly constructed role carries no paid-membership terms at all.
    /// </summary>
    [Fact]
    public void Role_CarriesNoPaidMembershipTermsByDefault()
    {
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
    /// A newly constructed role is neither public nor automatically assigned.
    /// </summary>
    [Fact]
    public void Role_IsNeitherPublicNorAutomaticByDefault()
    {
        Role role = new() { RoleId = 1, RoleName = "Administrators" };

        role.IsPublic.Should().BeFalse();
        role.AutoAssignment.Should().BeFalse();
        role.RoleName.Should().Be("Administrators");
        new Role { RoleId = 2 }.RoleName.Should().BeEmpty("the name column is not nullable");
    }

    /// <summary>
    /// A role may belong to the installation rather than to a tenant.
    /// </summary>
    [Fact]
    public void Role_PortalOwnershipIsOptional()
    {
        Role hostRole = new() { RoleId = 1, RoleName = "Superusers" };
        Role tenantRole = new() { RoleId = 2, PortalId = 0, RoleName = "Registered Users" };

        hostRole.PortalId.Should().BeNull();
        tenantRole.PortalId.Should().Be(0);
    }

    /// <summary>
    /// The billing frequency members carry the characters the column stores.
    /// </summary>
    /// <param name="frequency">The member under test.</param>
    /// <param name="expected">The character the column holds for it.</param>
    [Theory]
    [InlineData(BillingFrequency.None, 'N')]
    [InlineData(BillingFrequency.OneTime, 'O')]
    [InlineData(BillingFrequency.Day, 'D')]
    [InlineData(BillingFrequency.Week, 'W')]
    [InlineData(BillingFrequency.Month, 'M')]
    [InlineData(BillingFrequency.Year, 'Y')]
    public void BillingFrequency_MembersCarryTheStoredCharacter(BillingFrequency frequency, char expected)
    {
        ((char)(ushort)frequency).Should().Be(
            expected,
            "the persistence layer converts by casting the member to a character, so the member value "
            + "and the stored character are the same thing");

        ((BillingFrequency)expected).Should().Be(frequency, "and the conversion is reversible");
    }

    /// <summary>
    /// Zero is not a billing frequency, so the type must never be used in a non-nullable position.
    /// </summary>
    [Fact]
    public void BillingFrequency_HasNoZeroMember()
    {
        Enum.IsDefined(default(BillingFrequency)).Should().BeFalse(
            "no stored character has the code point zero, so a non-nullable field of this type left at "
            + "its CLR default would hold a value the schema cannot represent - which is why every "
            + "frequency column and property is nullable");

        Enum.IsDefined(BillingFrequency.None).Should().BeTrue();
        Enum.GetValues<BillingFrequency>().Should().HaveCount(6);
    }

    /// <summary>
    /// The absence of a frequency and the 'N' frequency are different states.
    /// </summary>
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
    /// The membership status enumeration is ordinal, unlike the billing frequency.
    /// </summary>
    [Fact]
    public void RoleStatus_IsOrdinal()
    {
        ((int)RoleStatus.Pending).Should().Be(0);
        ((int)RoleStatus.Active).Should().Be(1);
        ((int)RoleStatus.Expired).Should().Be(2);
        Enum.IsDefined(default(RoleStatus)).Should().BeTrue();
    }

    /// <summary>
    /// An assignment carries no window and no trial flag until one is supplied.
    /// </summary>
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
    /// A newly constructed role and group expose empty collections rather than null ones.
    /// </summary>
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

    /// <summary>
    /// Attaching a role to a group through the navigation leaves both sides consistent.
    /// </summary>
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
}
