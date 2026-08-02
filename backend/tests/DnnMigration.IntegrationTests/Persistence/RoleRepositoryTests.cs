using DnnMigration.Domain.Abstractions.Repositories;
using DnnMigration.Domain.Common;
using DnnMigration.Domain.Entities;
using DnnMigration.Domain.Enums;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DnnMigration.IntegrationTests.Persistence;

/// <summary>
/// Covers the role repository, its groups and its account assignments.
/// </summary>
/// <remarks>
/// <para>
/// Almost every assertion here works inside a tenant this suite creates through the repository rather than
/// through the tenant endpoint. That is deliberate: creating a tenant through the endpoint also provisions
/// three default roles, which would make "the roles of this tenant" a moving target and would leave any
/// count or ordering assertion dependent on which other suite happened to run first.
/// </para>
/// <para>
/// Role identifiers begin at zero in this schema, so a role identifier of zero is a real role rather than an
/// unset value. The assertions below never treat it as absent, and the assignment created through the
/// navigation property exists specifically to prove that a brand-new role can be referenced before its
/// identifier is known.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
[Collection(IntegrationTestCollection.Name)]
public sealed class RoleRepositoryTests
{
    private const int UnknownRoleId = 987654;
    private const int UnknownPortalId = 987654;
    private const int UnknownRoleGroupId = 987654;

    private readonly ApiTestFixture _fixture;

    /// <summary>Initialises a new instance of the <see cref="RoleRepositoryTests"/> class.</summary>
    /// <param name="fixture">The shared host and database.</param>
    public RoleRepositoryTests(ApiTestFixture fixture) => _fixture = fixture;

    /// <summary>The seeded administrators role reads back with its stored values.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task GetAsync_ReturnsTheSeededRole()
    {
        using IServiceScope scope = _fixture.Services.CreateScope();
        IRoleRepository roles = scope.ServiceProvider.GetRequiredService<IRoleRepository>();

        Role? role = await roles.GetAsync(_fixture.Seed.AdministratorRoleId);

        role.Should().NotBeNull();
        role!.RoleName.Should().Be(IntegrationSeed.AdministratorsRoleName);
        role.PortalId.Should().Be(_fixture.Seed.PortalId);
        role.IsPublic.Should().BeFalse();
        role.AutoAssignment.Should().BeFalse();
        role.RoleGroupId.Should().BeNull();
        role.RoleGroup.Should().BeNull();
        role.BillingFrequency.Should().Be(BillingFrequency.None);
        role.TrialFrequency.Should().Be(BillingFrequency.None);
    }

    /// <summary>
    /// The first role of an installation carries the identifier zero, and it is treated as an identifier
    /// rather than as an unset value.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// <c>Roles.RoleID</c> is declared <c>IDENTITY(0, 1)</c>, so zero collides with the value an unassigned
    /// integer holds. Anything that read zero as "no role" would make the administrators role of every
    /// installation unaddressable, which is the most privileged role there is.
    /// </remarks>
    [Fact]
    public async Task GetAsync_TreatsTheZeroIdentitySeedAsAnIdentifier()
    {
        _fixture.Seed.AdministratorRoleId.Should().Be(0, "it is the first role of a freshly created database");

        using IServiceScope scope = _fixture.Services.CreateScope();
        IRoleRepository roles = scope.ServiceProvider.GetRequiredService<IRoleRepository>();

        Role? role = await roles.GetAsync(0);

        role.Should().NotBeNull();
        role!.RoleName.Should().Be(IntegrationSeed.AdministratorsRoleName);
    }

    /// <summary>An unknown identifier produces nothing rather than an exception.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task GetAsync_WithAnUnknownIdentifier_ReturnsNull()
    {
        using IServiceScope scope = _fixture.Services.CreateScope();
        IRoleRepository roles = scope.ServiceProvider.GetRequiredService<IRoleRepository>();

        (await roles.GetAsync(UnknownRoleId)).Should().BeNull();
        (await roles.GetGroupAsync(UnknownRoleGroupId)).Should().BeNull();
    }

    /// <summary>A role name resolves irrespective of case, and only within its own tenant.</summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// Role names are unique per tenant rather than per installation, so two tenants may each hold a role
    /// called the same thing. An unscoped lookup would hand one tenant's administrator role to another
    /// tenant's request.
    /// </remarks>
    [Fact]
    public async Task GetByNameAsync_IgnoresCaseAndIsScopedToTheTenant()
    {
        int otherPortalId = await CreatePortalAsync();
        string marker = Suffix();
        string roleName = FormattableString.Invariant($"Shared {marker}");
        int mine = await CreateRoleAsync(otherPortalId, roleName);

        try
        {
            using IServiceScope scope = _fixture.Services.CreateScope();
            IRoleRepository roles = scope.ServiceProvider.GetRequiredService<IRoleRepository>();

            Role? upper = await roles.GetByNameAsync(otherPortalId, roleName.ToUpperInvariant());
            upper.Should().NotBeNull();
            upper!.RoleId.Should().Be(mine);

            Role? padded = await roles.GetByNameAsync(otherPortalId, "  " + roleName + "  ");
            padded.Should().NotBeNull();
            padded!.RoleId.Should().Be(mine);

            (await roles.GetByNameAsync(_fixture.Seed.PortalId, roleName)).Should()
                .BeNull("the role belongs to another tenant");
            (await roles.GetByNameAsync(otherPortalId, IntegrationSeed.AdministratorsRoleName)).Should()
                .BeNull("this tenant was created without the default roles");
        }
        finally
        {
            await RemoveRoleAsync(mine);
            await RemovePortalAsync(otherPortalId);
        }
    }

    /// <summary>The name check is per tenant and can exclude the role being renamed.</summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// The exclusion exists so that saving a role without changing its name is not reported as a collision
    /// with itself.
    /// </remarks>
    [Fact]
    public async Task RoleNameExistsAsync_IsScopedToTheTenantAndCanExcludeOneRole()
    {
        int portalId = await CreatePortalAsync();
        string roleName = FormattableString.Invariant($"Unique {Suffix()}");
        int roleId = await CreateRoleAsync(portalId, roleName);

        try
        {
            using IServiceScope scope = _fixture.Services.CreateScope();
            IRoleRepository roles = scope.ServiceProvider.GetRequiredService<IRoleRepository>();

            (await roles.RoleNameExistsAsync(portalId, roleName)).Should().BeTrue();
            (await roles.RoleNameExistsAsync(portalId, roleName.ToUpperInvariant())).Should().BeTrue();
            (await roles.RoleNameExistsAsync(portalId, roleName, excludingRoleId: roleId)).Should().BeFalse();
            (await roles.RoleNameExistsAsync(_fixture.Seed.PortalId, roleName)).Should().BeFalse();
            (await roles.RoleNameExistsAsync(portalId, "Absent " + Suffix())).Should().BeFalse();
        }
        finally
        {
            await RemoveRoleAsync(roleId);
            await RemovePortalAsync(portalId);
        }
    }

    /// <summary>Only the roles marked for automatic assignment are returned.</summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// This is the set every newly registered account joins, so a role appearing here by mistake would grant
    /// access to everyone who signs up.
    /// </remarks>
    [Fact]
    public async Task ListAutoAssignedAsync_ReturnsOnlyTheAutomaticRoles()
    {
        int portalId = await CreatePortalAsync();
        string marker = Suffix();
        int automaticA = await CreateRoleAsync(portalId, FormattableString.Invariant($"A {marker}"), autoAssignment: true);
        int automaticB = await CreateRoleAsync(portalId, FormattableString.Invariant($"B {marker}"), autoAssignment: true);
        int manual = await CreateRoleAsync(portalId, FormattableString.Invariant($"C {marker}"), autoAssignment: false);

        try
        {
            using IServiceScope scope = _fixture.Services.CreateScope();
            IRoleRepository roles = scope.ServiceProvider.GetRequiredService<IRoleRepository>();

            IReadOnlyList<Role> automatic = await roles.ListAutoAssignedAsync(portalId);

            automatic.Select(role => role.RoleId).Should().Equal(new[] { automaticA, automaticB });
            automatic.Select(role => role.RoleId).Should().NotContain(manual);

            // The seeded tenant provisions two automatic roles of its own, which confirms the filter is not
            // simply returning nothing.
            IReadOnlyList<Role> seeded = await roles.ListAutoAssignedAsync(_fixture.Seed.PortalId);
            seeded.Select(role => role.RoleName).Should()
                .Contain(IntegrationSeed.RegisteredUsersRoleName)
                .And.Contain(IntegrationSeed.SubscribersRoleName)
                .And.NotContain(IntegrationSeed.AdministratorsRoleName);
        }
        finally
        {
            await RemoveRoleAsync(automaticA);
            await RemoveRoleAsync(automaticB);
            await RemoveRoleAsync(manual);
            await RemovePortalAsync(portalId);
        }
    }

    /// <summary>An unpaged listing returns a tenant's roles by name and is confined to that tenant.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task ListAsync_Unpaged_ReturnsTheTenantsRolesByName()
    {
        int portalId = await CreatePortalAsync();
        string marker = Suffix();
        int third = await CreateRoleAsync(portalId, FormattableString.Invariant($"C {marker}"));
        int first = await CreateRoleAsync(portalId, FormattableString.Invariant($"A {marker}"));
        int second = await CreateRoleAsync(portalId, FormattableString.Invariant($"B {marker}"));

        try
        {
            using IServiceScope scope = _fixture.Services.CreateScope();
            IRoleRepository roles = scope.ServiceProvider.GetRequiredService<IRoleRepository>();

            PagedResult<Role> page = await roles.ListAsync(portalId, null, 0, 0, null);

            page.IsUnpaged.Should().BeTrue();
            page.Items.Select(role => role.RoleId).Should().Equal(new[] { first, second, third });
            page.TotalCount.Should().Be(3);

            PagedResult<Role> elsewhere = await roles.ListAsync(UnknownPortalId, null, 0, 0, null);
            elsewhere.Items.Should().BeEmpty();
        }
        finally
        {
            await RemoveRoleAsync(first);
            await RemoveRoleAsync(second);
            await RemoveRoleAsync(third);
            await RemovePortalAsync(portalId);
        }
    }

    /// <summary>The reported total counts every match, not just the page that was returned.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task ListAsync_Paged_ReportsTheTotalIndependentlyOfThePageSize()
    {
        int portalId = await CreatePortalAsync();
        string marker = Suffix();
        int first = await CreateRoleAsync(portalId, FormattableString.Invariant($"A {marker}"));
        int second = await CreateRoleAsync(portalId, FormattableString.Invariant($"B {marker}"));
        int third = await CreateRoleAsync(portalId, FormattableString.Invariant($"C {marker}"));

        try
        {
            using IServiceScope scope = _fixture.Services.CreateScope();
            IRoleRepository roles = scope.ServiceProvider.GetRequiredService<IRoleRepository>();

            PagedResult<Role> firstPage = await roles.ListAsync(portalId, null, 0, 2, null);
            PagedResult<Role> secondPage = await roles.ListAsync(portalId, null, 1, 2, null);

            firstPage.TotalCount.Should().Be(3);
            firstPage.PageIndex.Should().Be(0);
            firstPage.PageSize.Should().Be(2);
            firstPage.TotalPages.Should().Be(2);
            firstPage.HasNextPage.Should().BeTrue();
            firstPage.Items.Select(role => role.RoleId).Should().Equal(new[] { first, second });

            secondPage.TotalCount.Should().Be(3);
            secondPage.HasNextPage.Should().BeFalse();
            secondPage.Items.Select(role => role.RoleId).Should().Equal(third);
        }
        finally
        {
            await RemoveRoleAsync(first);
            await RemoveRoleAsync(second);
            await RemoveRoleAsync(third);
            await RemovePortalAsync(portalId);
        }
    }

    /// <summary>The name filter matches a fragment anywhere in the value and ignores case.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task ListAsync_WithANameFragment_MatchesAnywhereAndIgnoresCase()
    {
        int portalId = await CreatePortalAsync();
        string marker = Suffix();
        int matching = await CreateRoleAsync(portalId, FormattableString.Invariant($"Lead {marker}"));
        int other = await CreateRoleAsync(portalId, FormattableString.Invariant($"Other {Suffix()}"));

        try
        {
            using IServiceScope scope = _fixture.Services.CreateScope();
            IRoleRepository roles = scope.ServiceProvider.GetRequiredService<IRoleRepository>();

            (await roles.ListAsync(portalId, null, 0, 0, marker)).Items
                .Should().ContainSingle().Which.RoleId.Should().Be(matching);

            (await roles.ListAsync(portalId, null, 0, 0, marker.ToUpperInvariant())).Items
                .Should().ContainSingle().Which.RoleId.Should().Be(matching);

            (await roles.ListAsync(portalId, null, 0, 0, "  " + marker + "  ")).Items
                .Should().ContainSingle().Which.RoleId.Should().Be(matching);

            (await roles.ListAsync(portalId, null, 0, 0, "no-role-bears-this")).Items.Should().BeEmpty();

            other.Should().NotBe(matching);
        }
        finally
        {
            await RemoveRoleAsync(matching);
            await RemoveRoleAsync(other);
            await RemovePortalAsync(portalId);
        }
    }

    /// <summary>A group filter selects that group's roles and the group navigation is loaded.</summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// The group name is projected alongside each role, so the navigation has to be loaded by the listing
    /// rather than left for a caller to fetch one row at a time.
    /// </remarks>
    [Fact]
    public async Task ListAsync_WithAGroupFilter_SelectsThatGroupAndLoadsItsNavigation()
    {
        int portalId = await CreatePortalAsync();
        string marker = Suffix();
        string groupName = FormattableString.Invariant($"Group {marker}");
        int groupId = await CreateGroupAsync(portalId, groupName);
        int grouped = await CreateRoleAsync(portalId, FormattableString.Invariant($"A {marker}"), roleGroupId: groupId);
        int ungrouped = await CreateRoleAsync(portalId, FormattableString.Invariant($"B {marker}"));

        try
        {
            using IServiceScope scope = _fixture.Services.CreateScope();
            IRoleRepository roles = scope.ServiceProvider.GetRequiredService<IRoleRepository>();

            PagedResult<Role> inGroup = await roles.ListAsync(portalId, groupId, 0, 0, null);

            inGroup.Items.Should().ContainSingle();
            inGroup.Items[0].RoleId.Should().Be(grouped);
            inGroup.Items[0].RoleGroupId.Should().Be(groupId);
            inGroup.Items[0].RoleGroup.Should().NotBeNull();
            inGroup.Items[0].RoleGroup!.RoleGroupName.Should().Be(groupName);

            PagedResult<Role> everything = await roles.ListAsync(portalId, null, 0, 0, null);
            everything.Items.Select(role => role.RoleId).Should().BeEquivalentTo(new[] { grouped, ungrouped });

            PagedResult<Role> emptyGroup = await roles.ListAsync(portalId, UnknownRoleGroupId, 0, 0, null);
            emptyGroup.Items.Should().BeEmpty();
        }
        finally
        {
            await RemoveRoleAsync(grouped);
            await RemoveRoleAsync(ungrouped);
            await RemoveGroupAsync(groupId);
            await RemovePortalAsync(portalId);
        }
    }

    /// <summary>Groups are created, read, listed, checked for duplicates and removed.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task Groups_RoundTripThroughTheRepository()
    {
        int portalId = await CreatePortalAsync();
        string marker = Suffix();
        string firstName = FormattableString.Invariant($"A Group {marker}");
        string secondName = FormattableString.Invariant($"B Group {marker}");
        int firstId = await CreateGroupAsync(portalId, firstName);
        int secondId = await CreateGroupAsync(portalId, secondName);

        try
        {
            using (IServiceScope scope = _fixture.Services.CreateScope())
            {
                IRoleRepository roles = scope.ServiceProvider.GetRequiredService<IRoleRepository>();

                RoleGroup? group = await roles.GetGroupAsync(firstId);
                group.Should().NotBeNull();
                group!.RoleGroupName.Should().Be(firstName);
                group.PortalId.Should().Be(portalId);

                IReadOnlyList<RoleGroup> listed = await roles.ListGroupsAsync(portalId);
                listed.Select(entry => entry.RoleGroupId).Should().Equal(new[] { firstId, secondId });

                (await roles.ListGroupsAsync(UnknownPortalId)).Should().BeEmpty();

                (await roles.GroupNameExistsAsync(portalId, firstName)).Should().BeTrue();
                (await roles.GroupNameExistsAsync(portalId, firstName.ToUpperInvariant())).Should().BeTrue();
                (await roles.GroupNameExistsAsync(portalId, firstName, excludingRoleGroupId: firstId)).Should().BeFalse();
                (await roles.GroupNameExistsAsync(_fixture.Seed.PortalId, firstName)).Should()
                    .BeFalse("group names are unique per tenant, not per installation");
            }

            await RemoveGroupAsync(firstId);

            using IServiceScope after = _fixture.Services.CreateScope();
            IRoleRepository remaining = after.ServiceProvider.GetRequiredService<IRoleRepository>();

            (await remaining.GetGroupAsync(firstId)).Should().BeNull();
            (await remaining.ListGroupsAsync(portalId)).Select(entry => entry.RoleGroupId).Should().Equal(secondId);
        }
        finally
        {
            await RemoveGroupAsync(firstId);
            await RemoveGroupAsync(secondId);
            await RemovePortalAsync(portalId);
        }
    }

    /// <summary>The seeded administrator's assignment to the administrators role is readable.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task GetAssignmentAsync_ReturnsTheSeededAssignment()
    {
        using IServiceScope scope = _fixture.Services.CreateScope();
        IRoleRepository roles = scope.ServiceProvider.GetRequiredService<IRoleRepository>();

        UserRole? assignment = await roles.GetAssignmentAsync(
            _fixture.Seed.AdministratorRoleId,
            _fixture.Seed.AdminUserId);

        assignment.Should().NotBeNull();
        assignment!.UserId.Should().Be(_fixture.Seed.AdminUserId);
        assignment.RoleId.Should().Be(_fixture.Seed.AdministratorRoleId);

        (await roles.GetAssignmentAsync(UnknownRoleId, _fixture.Seed.AdminUserId)).Should().BeNull();
        (await roles.GetAssignmentAsync(_fixture.Seed.AdministratorRoleId, 987654)).Should().BeNull();
    }

    /// <summary>A role's members are listed by account, paged, with the account navigation loaded.</summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// The membership grid shows the account rather than its identifier, so the navigation is loaded by the
    /// listing. Without it a page of members would cost one extra read per row.
    /// </remarks>
    [Fact]
    public async Task ListAssignmentsAsync_ListsMembersByAccountAndLoadsTheNavigation()
    {
        int portalId = await CreatePortalAsync();
        int roleId = await CreateRoleAsync(portalId, FormattableString.Invariant($"Members {Suffix()}"));

        try
        {
            await AddAssignmentAsync(roleId, _fixture.Seed.MemberUserId);
            await AddAssignmentAsync(roleId, _fixture.Seed.AdminUserId);

            int lowerUserId = Math.Min(_fixture.Seed.MemberUserId, _fixture.Seed.AdminUserId);
            int higherUserId = Math.Max(_fixture.Seed.MemberUserId, _fixture.Seed.AdminUserId);

            using IServiceScope scope = _fixture.Services.CreateScope();
            IRoleRepository roles = scope.ServiceProvider.GetRequiredService<IRoleRepository>();

            PagedResult<UserRole> all = await roles.ListAssignmentsAsync(roleId, 0, 0);

            all.Items.Select(assignment => assignment.UserId).Should().Equal(new[] { lowerUserId, higherUserId });
            all.Items[0].User.Should().NotBeNull();
            all.Items[0].User!.UserId.Should().Be(lowerUserId);

            PagedResult<UserRole> firstPage = await roles.ListAssignmentsAsync(roleId, 0, 1);
            firstPage.TotalCount.Should().Be(2);
            firstPage.Items.Should().ContainSingle().Which.UserId.Should().Be(lowerUserId);

            PagedResult<UserRole> secondPage = await roles.ListAssignmentsAsync(roleId, 1, 1);
            secondPage.TotalCount.Should().Be(2);
            secondPage.Items.Should().ContainSingle().Which.UserId.Should().Be(higherUserId);

            (await roles.ListAssignmentsAsync(UnknownRoleId, 0, 0)).Items.Should().BeEmpty();
        }
        finally
        {
            await RemoveRoleAsync(roleId);
            await RemovePortalAsync(portalId);
        }
    }

    /// <summary>An account's assignments are confined to the roles of the tenant being asked about.</summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// One account may hold roles in several tenants at once. Listing them all when one tenant was asked
    /// about would disclose the role structure of every other tenant the account belongs to.
    /// </remarks>
    [Fact]
    public async Task ListUserAssignmentsAsync_IsConfinedToTheTenantsRoles()
    {
        int portalId = await CreatePortalAsync();
        int roleId = await CreateRoleAsync(portalId, FormattableString.Invariant($"Elsewhere {Suffix()}"));

        try
        {
            await AddAssignmentAsync(roleId, _fixture.Seed.MemberUserId);

            using IServiceScope scope = _fixture.Services.CreateScope();
            IRoleRepository roles = scope.ServiceProvider.GetRequiredService<IRoleRepository>();

            IReadOnlyList<UserRole> here = await roles.ListUserAssignmentsAsync(portalId, _fixture.Seed.MemberUserId);

            here.Should().ContainSingle();
            here[0].RoleId.Should().Be(roleId);
            here[0].Role.Should().NotBeNull();
            here[0].Role!.PortalId.Should().Be(portalId);

            IReadOnlyList<UserRole> inTheSeededTenant = await roles.ListUserAssignmentsAsync(
                _fixture.Seed.PortalId,
                _fixture.Seed.MemberUserId);

            inTheSeededTenant.Select(assignment => assignment.RoleId).Should()
                .Contain(_fixture.Seed.RegisteredRoleId)
                .And.NotContain(roleId);

            (await roles.ListUserAssignmentsAsync(UnknownPortalId, _fixture.Seed.MemberUserId)).Should().BeEmpty();
        }
        finally
        {
            await RemoveRoleAsync(roleId);
            await RemovePortalAsync(portalId);
        }
    }

    /// <summary>An assignment is added, amended and removed.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task Assignment_IsAddedAmendedAndRemoved()
    {
        int portalId = await CreatePortalAsync();
        int roleId = await CreateRoleAsync(portalId, FormattableString.Invariant($"Lifecycle {Suffix()}"));
        DateTime expiry = new(2027, 6, 1, 0, 0, 0, DateTimeKind.Utc);

        try
        {
            await AddAssignmentAsync(roleId, _fixture.Seed.MemberUserId);

            using (IServiceScope amending = _fixture.Services.CreateScope())
            {
                IRoleRepository roles = amending.ServiceProvider.GetRequiredService<IRoleRepository>();
                IUnitOfWork unitOfWork = amending.ServiceProvider.GetRequiredService<IUnitOfWork>();

                UserRole? assignment = await roles.GetAssignmentAsync(roleId, _fixture.Seed.MemberUserId);
                assignment.Should().NotBeNull();
                assignment!.ExpiryDate.Should().BeNull();

                assignment.ExpiryDate = expiry;
                assignment.IsTrialUsed = true;
                await unitOfWork.SaveChangesAsync();
            }

            using (IServiceScope reading = _fixture.Services.CreateScope())
            {
                IRoleRepository roles = reading.ServiceProvider.GetRequiredService<IRoleRepository>();

                UserRole? amended = await roles.GetAssignmentAsync(roleId, _fixture.Seed.MemberUserId);
                amended.Should().NotBeNull();
                amended!.ExpiryDate.Should().Be(expiry);
                amended.IsTrialUsed.Should().BeTrue();
            }

            using (IServiceScope removing = _fixture.Services.CreateScope())
            {
                IRoleRepository roles = removing.ServiceProvider.GetRequiredService<IRoleRepository>();
                IUnitOfWork unitOfWork = removing.ServiceProvider.GetRequiredService<IUnitOfWork>();

                UserRole? doomed = await roles.GetAssignmentAsync(roleId, _fixture.Seed.MemberUserId);
                roles.RemoveAssignment(doomed!);
                await unitOfWork.SaveChangesAsync();
            }

            using IServiceScope after = _fixture.Services.CreateScope();
            IRoleRepository remaining = after.ServiceProvider.GetRequiredService<IRoleRepository>();

            (await remaining.GetAssignmentAsync(roleId, _fixture.Seed.MemberUserId)).Should().BeNull();
            (await remaining.GetAsync(roleId)).Should().NotBeNull("removing a member does not remove the role");
        }
        finally
        {
            await RemoveRoleAsync(roleId);
            await RemovePortalAsync(portalId);
        }
    }

    /// <summary>
    /// A brand-new role and its first member are saved together by referring to the role through its
    /// navigation rather than through its identifier.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// This is how a role that automatically assigns itself enrols the existing membership at the moment it is
    /// created. The identifier is not known until the save happens, and it may turn out to be zero, which is
    /// indistinguishable from an unset value. Referring to the role object instead leaves the store to fill in
    /// the identifier, so the pattern is correct whatever value the identity column produces.
    /// </remarks>
    [Fact]
    public async Task Add_ThroughTheNavigation_SavesARoleAndItsFirstMemberTogether()
    {
        int portalId = await CreatePortalAsync();
        int roleId;

        try
        {
            using (IServiceScope creating = _fixture.Services.CreateScope())
            {
                IRoleRepository roles = creating.ServiceProvider.GetRequiredService<IRoleRepository>();
                IUnitOfWork unitOfWork = creating.ServiceProvider.GetRequiredService<IUnitOfWork>();

                Role role = new()
                {
                    PortalId = portalId,
                    RoleName = FormattableString.Invariant($"Automatic {Suffix()}"),
                    AutoAssignment = true,
                    IsPublic = true,
                };

                roles.Add(role);
                roles.AddAssignment(new UserRole { UserId = _fixture.Seed.MemberUserId, Role = role });

                await unitOfWork.SaveChangesAsync();

                roleId = role.RoleId;
            }

            using IServiceScope reading = _fixture.Services.CreateScope();
            IRoleRepository confirming = reading.ServiceProvider.GetRequiredService<IRoleRepository>();

            (await confirming.GetAsync(roleId)).Should().NotBeNull();

            UserRole? assignment = await confirming.GetAssignmentAsync(roleId, _fixture.Seed.MemberUserId);
            assignment.Should().NotBeNull();
            assignment!.RoleId.Should().Be(roleId, "the store filled the identifier in from the navigation");
        }
        finally
        {
            await RemovePortalAsync(portalId);
        }
    }

    /// <summary>Removing a role takes its memberships with it.</summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// The cascade is declared in the schema rather than performed by the application. Leaving the membership
    /// rows behind would leave assignments naming a role that no longer exists, and the permission evaluation
    /// resolves role names by joining through exactly those rows.
    /// </remarks>
    [Fact]
    public async Task Remove_TakesTheRolesMembershipsWithIt()
    {
        int portalId = await CreatePortalAsync();
        int roleId = await CreateRoleAsync(portalId, FormattableString.Invariant($"Doomed {Suffix()}"));

        try
        {
            await AddAssignmentAsync(roleId, _fixture.Seed.MemberUserId);

            int before = await CountAssignmentsAsync(roleId);
            before.Should().Be(1);

            await RemoveRoleAsync(roleId);

            using IServiceScope scope = _fixture.Services.CreateScope();
            IRoleRepository roles = scope.ServiceProvider.GetRequiredService<IRoleRepository>();

            (await roles.GetAsync(roleId)).Should().BeNull();
            (await CountAssignmentsAsync(roleId)).Should().Be(0);
            (await roles.GetAssignmentAsync(roleId, _fixture.Seed.MemberUserId)).Should().BeNull();

            // The account itself survives; only its membership of the removed role is gone.
            IUserRepository users = scope.ServiceProvider.GetRequiredService<IUserRepository>();
            (await users.GetAsync(_fixture.Seed.PortalId, _fixture.Seed.MemberUserId)).Should().NotBeNull();
        }
        finally
        {
            await RemovePortalAsync(portalId);
        }
    }

    /// <summary>Creates a bare tenant through the repository.</summary>
    /// <returns>The identifier the store assigned.</returns>
    private async Task<int> CreatePortalAsync()
    {
        using IServiceScope scope = _fixture.Services.CreateScope();
        IPortalRepository portals = scope.ServiceProvider.GetRequiredService<IPortalRepository>();
        IUnitOfWork unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        Portal portal = new()
        {
            PortalName = FormattableString.Invariant($"Role Tenant {Suffix()}"),
            UserRegistration = UserRegistrationMode.PrivateRegistration,
            BannerAdvertising = BannerAdvertisingMode.None,
            Currency = "USD",
            HostFee = 0m,
            HostSpace = 0,
            PortalGuid = Guid.NewGuid(),
            DefaultLanguage = "en-US",
            TimeZoneOffset = -8,
            HomeDirectory = string.Empty,
            PageQuota = 0,
            UserQuota = 0,
        };

        portals.Add(portal);
        await unitOfWork.SaveChangesAsync();

        return portal.PortalId;
    }

    /// <summary>Removes a tenant created by this suite.</summary>
    /// <param name="portalId">The tenant to remove.</param>
    /// <returns>A task that completes when the tenant is gone.</returns>
    private async Task RemovePortalAsync(int portalId)
    {
        using IServiceScope scope = _fixture.Services.CreateScope();
        IPortalRepository portals = scope.ServiceProvider.GetRequiredService<IPortalRepository>();
        IUnitOfWork unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        Portal? doomed = await portals.GetAsync(portalId);

        if (doomed is not null)
        {
            portals.Remove(doomed);
            await unitOfWork.SaveChangesAsync();
        }
    }

    /// <summary>Creates a role through the repository.</summary>
    /// <param name="portalId">The owning tenant.</param>
    /// <param name="roleName">The role name, which must be unique within the tenant.</param>
    /// <param name="autoAssignment">Whether new accounts join the role automatically.</param>
    /// <param name="roleGroupId">The group the role belongs to, if any.</param>
    /// <returns>The identifier the store assigned.</returns>
    private async Task<int> CreateRoleAsync(
        int portalId,
        string roleName,
        bool autoAssignment = false,
        int? roleGroupId = null)
    {
        using IServiceScope scope = _fixture.Services.CreateScope();
        IRoleRepository roles = scope.ServiceProvider.GetRequiredService<IRoleRepository>();
        IUnitOfWork unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        Role role = new()
        {
            PortalId = portalId,
            RoleName = roleName,
            Description = "Created by the persistence role suite.",
            AutoAssignment = autoAssignment,
            IsPublic = false,
            RoleGroupId = roleGroupId,
        };

        roles.Add(role);
        await unitOfWork.SaveChangesAsync();

        return role.RoleId;
    }

    /// <summary>Removes a role created by this suite.</summary>
    /// <param name="roleId">The role to remove.</param>
    /// <returns>A task that completes when the role is gone.</returns>
    private async Task RemoveRoleAsync(int roleId)
    {
        using IServiceScope scope = _fixture.Services.CreateScope();
        IRoleRepository roles = scope.ServiceProvider.GetRequiredService<IRoleRepository>();
        IUnitOfWork unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        Role? doomed = await roles.GetAsync(roleId);

        if (doomed is not null)
        {
            roles.Remove(doomed);
            await unitOfWork.SaveChangesAsync();
        }
    }

    /// <summary>Creates a role group through the repository.</summary>
    /// <param name="portalId">The owning tenant.</param>
    /// <param name="roleGroupName">The group name, which must be unique within the tenant.</param>
    /// <returns>The identifier the store assigned.</returns>
    private async Task<int> CreateGroupAsync(int portalId, string roleGroupName)
    {
        using IServiceScope scope = _fixture.Services.CreateScope();
        IRoleRepository roles = scope.ServiceProvider.GetRequiredService<IRoleRepository>();
        IUnitOfWork unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        RoleGroup group = new()
        {
            PortalId = portalId,
            RoleGroupName = roleGroupName,
            Description = "Created by the persistence role suite.",
        };

        roles.AddGroup(group);
        await unitOfWork.SaveChangesAsync();

        return group.RoleGroupId;
    }

    /// <summary>Removes a role group created by this suite, if it is still present.</summary>
    /// <param name="roleGroupId">The group to remove.</param>
    /// <returns>A task that completes when the group is gone.</returns>
    private async Task RemoveGroupAsync(int roleGroupId)
    {
        using IServiceScope scope = _fixture.Services.CreateScope();
        IRoleRepository roles = scope.ServiceProvider.GetRequiredService<IRoleRepository>();
        IUnitOfWork unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        RoleGroup? doomed = await roles.GetGroupAsync(roleGroupId);

        if (doomed is not null)
        {
            roles.RemoveGroup(doomed);
            await unitOfWork.SaveChangesAsync();
        }
    }

    /// <summary>Records a membership of a role.</summary>
    /// <param name="roleId">The role to join.</param>
    /// <param name="userId">The account joining it.</param>
    /// <returns>A task that completes when the membership exists.</returns>
    private async Task AddAssignmentAsync(int roleId, int userId)
    {
        using IServiceScope scope = _fixture.Services.CreateScope();
        IRoleRepository roles = scope.ServiceProvider.GetRequiredService<IRoleRepository>();
        IUnitOfWork unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        roles.AddAssignment(new UserRole
        {
            UserId = userId,
            RoleId = roleId,
            IsTrialUsed = false,
        });

        await unitOfWork.SaveChangesAsync();
    }

    /// <summary>Counts the membership rows of a role straight out of the store.</summary>
    /// <param name="roleId">The role to count.</param>
    /// <returns>The number of membership rows.</returns>
    private Task<int> CountAssignmentsAsync(int roleId)
    {
        return _fixture.Database.ScalarAsync<int>(
            "SELECT COUNT(*) FROM [dbo].[UserRoles] WHERE [RoleID] = @roleId",
            new Dictionary<string, object?> { ["roleId"] = roleId });
    }

    /// <summary>
    /// The two currency columns round-trip a value larger than a two-decimal-place column could
    /// hold, which is what distinguishes the legacy <c>money</c> type from a narrower numeric type.
    /// </summary>
    /// <remarks>
    /// This test exists because a mapping that is correct in the entity configuration can still be
    /// contradicted by the schema the suite runs against, and nothing else here would notice. The
    /// legacy terminal DDL declares both columns as <c>money</c> -
    /// <c>01.00.04.SqlDataProvider:1326</c> and <c>01.00.05.SqlDataProvider:2752</c> rebuild
    /// <c>Roles</c> with "ServiceFee money NULL", and every procedure that carries the value through
    /// <c>04.00.04.SqlDataProvider</c> declares "@ServiceFee money". A narrower column such as
    /// <c>decimal(5,2)</c> accepts every fee the rest of this suite uses, because those are all
    /// 9.99 or 19.99, and overflows only on a figure no other test supplies. The value below is
    /// deliberately above that ceiling so the column type itself is under assertion, and the read
    /// happens in a fresh scope so the value is returned by the database rather than by the change
    /// tracker.
    /// </remarks>
    /// <returns>A task representing the test.</returns>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task MoneyColumns_RoundTripAValueBeyondATwoDecimalPlaceCeiling()
    {
        const decimal serviceFee = 12345.67m;
        const decimal trialFee = 98765.43m;

        int portalId = await CreatePortalAsync();
        string roleName = FormattableString.Invariant($"Priced {Suffix()}");
        int roleId;

        using (IServiceScope writeScope = _fixture.Services.CreateScope())
        {
            IRoleRepository writeRoles = writeScope.ServiceProvider.GetRequiredService<IRoleRepository>();
            IUnitOfWork unitOfWork = writeScope.ServiceProvider.GetRequiredService<IUnitOfWork>();

            Role role = new()
            {
                PortalId = portalId,
                RoleName = roleName,
                Description = "Created by the persistence role suite.",
                ServiceFee = serviceFee,
                TrialFee = trialFee,
                BillingFrequency = BillingFrequency.Month,
                TrialFrequency = BillingFrequency.Week,
            };

            writeRoles.Add(role);
            await unitOfWork.SaveChangesAsync();
            roleId = role.RoleId;
        }

        try
        {
            using IServiceScope scope = _fixture.Services.CreateScope();
            IRoleRepository roles = scope.ServiceProvider.GetRequiredService<IRoleRepository>();

            Role? stored = await roles.GetAsync(roleId);

            stored.Should().NotBeNull();
            stored!.ServiceFee.Should().Be(serviceFee);
            stored.TrialFee.Should().Be(trialFee);

            // The single-character billing codes survive the same round-trip, which is the other
            // facet of this table that a convention-based mapping would get wrong.
            stored.BillingFrequency.Should().Be(BillingFrequency.Month);
            stored.TrialFrequency.Should().Be(BillingFrequency.Week);
        }
        finally
        {
            await RemoveRoleAsync(roleId);
            await RemovePortalAsync(portalId);
        }
    }

    /// <summary>Produces a short random suffix so concurrently executing suites cannot collide.</summary>
    /// <returns>A twelve-character suffix.</returns>
    private static string Suffix() => Guid.NewGuid().ToString("N")[..12];
}
