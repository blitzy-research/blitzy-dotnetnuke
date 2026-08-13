using DnnMigration.Domain.Abstractions.Repositories;
using DnnMigration.Domain.Common;
using DnnMigration.Domain.Entities;
using DnnMigration.Domain.Enums;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DnnMigration.IntegrationTests.Persistence;

/// <summary>Covers the role repository, its groups and its account assignments.</summary>
/// <remarks>
/// <para>
/// Almost every assertion here works inside a tenant this suite creates through the repository rather than
/// through the tenant endpoint. That is deliberate: creating a tenant through the endpoint also provisions
/// three default roles, which would make "the roles of this tenant" a moving target and would leave any
/// count or ordering assertion dependent on which other suite happened to run first.
/// </para>
/// <para>
/// Role identifiers begin at zero in this schema, so a role identifier of zero is a real role rather than
/// an unset value. The assertions below never treat it as absent, and the assignment created through the
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

    /// <summary>An account key no seeded or suite-created account bears.</summary>
    private const int UnknownUserId = 987654;

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

        Role? role = await roles.GetByIdAsync(_fixture.Seed.AdministratorRoleId, _fixture.Seed.PortalId);

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

        Role? role = await roles.GetByIdAsync(0, _fixture.Seed.PortalId);

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

        (await roles.GetByIdAsync(UnknownRoleId, _fixture.Seed.PortalId)).Should().BeNull();
        (await roles.GetRoleGroupAsync(_fixture.Seed.PortalId, UnknownRoleGroupId)).Should().BeNull();

        // A real role asked for under the wrong tenant is absent too: the terminal GetRole
        // (04.00.04.SqlDataProvider:L334-L335) makes the portal a condition, not a hint.
        (await roles.GetByIdAsync(_fixture.Seed.AdministratorRoleId, UnknownPortalId)).Should().BeNull();
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
    public async Task GetByNameAsync_SettlesUniquenessWithinTheTenant()
    {
        int portalId = await CreatePortalAsync();
        string roleName = FormattableString.Invariant($"Unique {Suffix()}");
        int roleId = await CreateRoleAsync(portalId, roleName);

        try
        {
            using IServiceScope scope = _fixture.Services.CreateScope();
            IRoleRepository roles = scope.ServiceProvider.GetRequiredService<IRoleRepository>();

            // Uniqueness needs no dedicated member. IX_RoleName is UNIQUE over (PortalID, RoleName), so
            // GetRoleByName can match at most one row and the row it matches IS the answer - including
            // which role holds the name, which a boolean could not report.
            Role? taken = await roles.GetByNameAsync(portalId, roleName);
            taken.Should().NotBeNull();
            taken!.RoleId.Should().Be(roleId, "the match names the role holding the name, so an edit can recognise itself");

            (await roles.GetByNameAsync(portalId, roleName.ToUpperInvariant())).Should().NotBeNull();
            (await roles.GetByNameAsync(_fixture.Seed.PortalId, roleName)).Should()
                .BeNull("role names are unique per tenant, not per installation");
            (await roles.GetByNameAsync(portalId, "Absent " + Suffix())).Should().BeNull();
        }
        finally
        {
            await RemoveRoleAsync(roleId);
            await RemovePortalAsync(portalId);
        }
    }

    /// <summary>Only the roles marked for automatic assignment are returned.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task GetByPortalIdAsync_CarriesTheAutoAssignmentFlag()
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

            // The membership provider had no auto-assigned procedure - it exposed GetPortalRoles(PortalId)
            // alone and the caller tested the column. The flag is therefore asserted on the rows this read
            // returns, which is where the legacy behaviour actually lived.
            List<Role> automatic = (await roles.GetByPortalIdAsync(portalId))
                .Where(role => role.AutoAssignment)
                .ToList();

            automatic.Select(role => role.RoleId).Should().Equal(new[] { automaticA, automaticB });
            automatic.Select(role => role.RoleId).Should().NotContain(manual);

            // The seeded tenant provisions two automatic roles of its own, which confirms the filter is not
            // simply returning nothing.
            List<Role> seeded = (await roles.GetByPortalIdAsync(_fixture.Seed.PortalId))
                .Where(role => role.AutoAssignment)
                .ToList();
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

    /// <summary>The tenant listing returns a tenant's roles by name and excludes other tenants.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task GetByPortalIdAsync_ReturnsTheTenantsRolesByName()
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

            IReadOnlyList<Role> page = await roles.GetByPortalIdAsync(portalId);

            // Ordered by name, matching the terminal GetPortalRoles ORDER BY R.RoleName
            // (04.08.00.SqlDataProvider:L41). The owned subset is selected because the same procedure also
            // admits installation-wide roles, which the next assertion covers explicitly.
            page.Where(role => role.PortalId == portalId).Select(role => role.RoleId)
                .Should().Equal(new[] { first, second, third });

            // Nothing belonging to another tenant may appear. Expressed as "no row owned by a different
            // portal" rather than as a positive predicate, so the assertion still means something when the
            // installation happens to define no host role at all.
            page.Where(role => role.PortalId != portalId && role.PortalId != null)
                .Should().BeEmpty("a tenant listing may not disclose another tenant's roles");

            IReadOnlyList<Role> elsewhere = await roles.GetByPortalIdAsync(UnknownPortalId);
            elsewhere.Where(role => role.PortalId != null).Should().BeEmpty();
            elsewhere.Select(role => role.RoleId).Should().NotContain(first);
        }
        finally
        {
            await RemoveRoleAsync(first);
            await RemoveRoleAsync(second);
            await RemoveRoleAsync(third);
            await RemovePortalAsync(portalId);
        }
    }

    /// <summary>The host-wide listing crosses tenant boundaries, as the argument-less legacy read did.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task GetAllAsync_ReturnsRolesFromEveryTenant()
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

            IReadOnlyList<Role> everything = await roles.GetAllAsync();

            // The three roles this test created, and the seeded tenant's own, are all present - which is
            // what "no filter" means and is exactly why this member is not the one a tenant-scoped caller
            // should use.
            everything.Select(role => role.RoleId).Should()
                .Contain(first).And.Contain(second).And.Contain(third)
                .And.Contain(_fixture.Seed.AdministratorRoleId);

            everything.Select(role => role.PortalId).Distinct().Should()
                .HaveCountGreaterThan(1, "the read spans tenants rather than being confined to one");
        }
        finally
        {
            await RemoveRoleAsync(first);
            await RemoveRoleAsync(second);
            await RemoveRoleAsync(third);
            await RemovePortalAsync(portalId);
        }
    }

    /// <summary>Only the tenant's public roles are offered for subscription.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task GetSubscribableRolesAsync_ReturnsOnlyThePublicRoles()
    {
        int portalId = await CreatePortalAsync();
        string marker = Suffix();
        int publicRole = await CreateRoleAsync(portalId, FormattableString.Invariant($"Public {marker}"), isPublic: true);
        int privateRole = await CreateRoleAsync(portalId, FormattableString.Invariant($"Private {marker}"));

        try
        {
            using IServiceScope scope = _fixture.Services.CreateScope();
            IRoleRepository roles = scope.ServiceProvider.GetRequiredService<IRoleRepository>();

            IReadOnlyList<Role> offered = await roles.GetSubscribableRolesAsync(
                portalId,
                _fixture.Seed.MemberUserId);

            offered.Select(role => role.RoleId).Should().Equal(publicRole);
            offered.Should().OnlyContain(role => role.IsPublic);
            offered.Select(role => role.RoleId).Should().NotContain(privateRole);

            // The account argument annotates the caller's own subscription state in the legacy
            // projection; it must not widen or narrow the set of roles offered.
            IReadOnlyList<Role> forAnotherAccount = await roles.GetSubscribableRolesAsync(
                portalId,
                _fixture.Seed.AdminUserId);
            forAnotherAccount.Select(role => role.RoleId).Should().Equal(publicRole);

            (await roles.GetSubscribableRolesAsync(UnknownPortalId, _fixture.Seed.MemberUserId))
                .Should().BeEmpty();
        }
        finally
        {
            await RemoveRoleAsync(publicRole);
            await RemoveRoleAsync(privateRole);
            await RemovePortalAsync(portalId);
        }
    }

    /// <summary>A group filter selects that group's roles and the group navigation is loaded.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task GetRolesByGroupAsync_SelectsThatGroupAndLoadsItsNavigation()
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

            IReadOnlyList<Role> inGroup = await roles.GetRolesByGroupAsync(groupId, portalId);

            inGroup.Should().ContainSingle();
            inGroup[0].RoleId.Should().Be(grouped);
            inGroup[0].RoleGroupId.Should().Be(groupId);
            inGroup[0].RoleGroup.Should().NotBeNull();
            inGroup[0].RoleGroup!.RoleGroupName.Should().Be(groupName);

            // The ungrouped role is reachable through the tenant listing but belongs to no group, so no
            // group read may return it.
            IReadOnlyList<Role> everything = await roles.GetByPortalIdAsync(portalId);
            everything.Where(role => role.PortalId == portalId).Select(role => role.RoleId)
                .Should().BeEquivalentTo(new[] { grouped, ungrouped });

            (await roles.GetRolesByGroupAsync(UnknownRoleGroupId, portalId)).Should().BeEmpty();

            // The portal is a condition as well as the group, so a real group asked for under another
            // tenant yields nothing.
            (await roles.GetRolesByGroupAsync(groupId, UnknownPortalId)).Should().BeEmpty();
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

                RoleGroup? group = await roles.GetRoleGroupAsync(portalId, firstId);
                group.Should().NotBeNull();
                group!.RoleGroupName.Should().Be(firstName);
                group.PortalId.Should().Be(portalId);

                // RoleGroups.PortalID is NOT NULL, so the portal is a genuine condition: the same key
                // asked for under another tenant is absent.
                (await roles.GetRoleGroupAsync(UnknownPortalId, firstId)).Should().BeNull();

                IReadOnlyList<RoleGroup> listed = await roles.GetRoleGroupsAsync(portalId);
                listed.Select(entry => entry.RoleGroupId).Should().Equal(new[] { firstId, secondId });

                (await roles.GetRoleGroupsAsync(UnknownPortalId)).Should().BeEmpty();

                listed.Should().Contain(entry => entry.RoleGroupName == firstName);
                (await roles.GetRoleGroupsAsync(_fixture.Seed.PortalId)).Should()
                    .NotContain(entry => entry.RoleGroupName == firstName,
                        "group names are unique per tenant, not per installation");
            }

            await RemoveGroupAsync(firstId);

            using IServiceScope after = _fixture.Services.CreateScope();
            IRoleRepository remaining = after.ServiceProvider.GetRequiredService<IRoleRepository>();

            (await remaining.GetRoleGroupAsync(portalId, firstId)).Should().BeNull();
            (await remaining.GetRoleGroupsAsync(portalId)).Select(entry => entry.RoleGroupId).Should().Equal(secondId);
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
    public async Task GetUserRoleAsync_ReturnsTheSeededAssignment()
    {
        using IServiceScope scope = _fixture.Services.CreateScope();
        IRoleRepository roles = scope.ServiceProvider.GetRequiredService<IRoleRepository>();

        UserRole? assignment = await roles.GetUserRoleAsync(
            _fixture.Seed.PortalId,
            _fixture.Seed.AdminUserId,
            _fixture.Seed.AdministratorRoleId);

        assignment.Should().NotBeNull();
        assignment!.UserId.Should().Be(_fixture.Seed.AdminUserId);
        assignment.RoleId.Should().Be(_fixture.Seed.AdministratorRoleId);

        (await roles.GetUserRoleAsync(_fixture.Seed.PortalId, _fixture.Seed.AdminUserId, UnknownRoleId))
            .Should().BeNull();
        (await roles.GetUserRoleAsync(_fixture.Seed.PortalId, UnknownUserId, _fixture.Seed.AdministratorRoleId))
            .Should().BeNull();

        // Dbo.UserRoles has no portal column, so the tenant anchor is taken from the role the assignment
        // points at. A real assignment asked about under another tenant is therefore absent, which is what
        // stops one tenant answering another tenant's membership question.
        (await roles.GetUserRoleAsync(UnknownPortalId, _fixture.Seed.AdminUserId, _fixture.Seed.AdministratorRoleId))
            .Should().BeNull();
    }

    /// <summary>Assignments resolve by login name, and the role name narrows the answer optionally.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task GetUserRolesByUsernameAsync_ResolvesByLoginNameAndNarrowsByRoleName()
    {
        int portalId = await CreatePortalAsync();
        string roleName = FormattableString.Invariant($"Named {Suffix()}");
        int roleId = await CreateRoleAsync(portalId, roleName);

        try
        {
            await AddAssignmentAsync(roleId, _fixture.Seed.MemberUserId);

            using IServiceScope scope = _fixture.Services.CreateScope();
            IRoleRepository roles = scope.ServiceProvider.GetRequiredService<IRoleRepository>();

            // A null role name narrows nothing.
            IReadOnlyList<UserRole> all = await roles.GetUserRolesByUsernameAsync(
                portalId,
                IntegrationSeed.MemberUserName,
                null);

            all.Select(assignment => assignment.RoleId).Should().Equal(roleId);

            // The login name is matched without regard to case, as the legacy collation did.
            (await roles.GetUserRolesByUsernameAsync(
                portalId,
                IntegrationSeed.MemberUserName.ToUpperInvariant(),
                null)).Should().ContainSingle();

            // A supplied role name narrows to it.
            (await roles.GetUserRolesByUsernameAsync(portalId, IntegrationSeed.MemberUserName, roleName))
                .Select(assignment => assignment.RoleId).Should().Equal(roleId);

            // An empty role name is a name, not a wildcard, so it matches no role here.
            (await roles.GetUserRolesByUsernameAsync(portalId, IntegrationSeed.MemberUserName, string.Empty))
                .Should().BeEmpty();

            (await roles.GetUserRolesByUsernameAsync(portalId, "no_such_account", null)).Should().BeEmpty();

            // Scoped through the role, so another tenant's question yields nothing.
            (await roles.GetUserRolesByUsernameAsync(UnknownPortalId, IntegrationSeed.MemberUserName, null))
                .Should().BeEmpty();
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
    public async Task GetUserRolesAsync_IsConfinedToTheTenantsRoles()
    {
        int portalId = await CreatePortalAsync();
        int roleId = await CreateRoleAsync(portalId, FormattableString.Invariant($"Elsewhere {Suffix()}"));

        try
        {
            await AddAssignmentAsync(roleId, _fixture.Seed.MemberUserId);

            using IServiceScope scope = _fixture.Services.CreateScope();
            IRoleRepository roles = scope.ServiceProvider.GetRequiredService<IRoleRepository>();

            IReadOnlyList<UserRole> here = await roles.GetUserRolesAsync(portalId, _fixture.Seed.MemberUserId);

            here.Should().ContainSingle();
            here[0].RoleId.Should().Be(roleId);
            here[0].Role.Should().NotBeNull();
            here[0].Role!.PortalId.Should().Be(portalId);

            IReadOnlyList<UserRole> inTheSeededTenant = await roles.GetUserRolesAsync(
                _fixture.Seed.PortalId,
                _fixture.Seed.MemberUserId);

            inTheSeededTenant.Select(assignment => assignment.RoleId).Should()
                .Contain(_fixture.Seed.RegisteredRoleId)
                .And.NotContain(roleId);

            (await roles.GetUserRolesAsync(UnknownPortalId, _fixture.Seed.MemberUserId)).Should().BeEmpty();

            // The role-shaped view of the same membership agrees with the assignment-shaped one.
            (await roles.GetRolesByUserIdAsync(_fixture.Seed.MemberUserId, portalId))
                .Select(role => role.RoleId).Should().Equal(roleId);
            (await roles.GetRolesByUserIdAsync(_fixture.Seed.MemberUserId, UnknownPortalId))
                .Should().BeEmpty();
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

                UserRole? assignment = await roles.GetUserRoleAsync(portalId, _fixture.Seed.MemberUserId, roleId);
                assignment.Should().NotBeNull();
                assignment!.ExpiryDate.Should().BeNull("an absent bound is unbounded, not a far-future date");

                assignment.ExpiryDate = expiry;
                assignment.IsTrialUsed = true;

                // The amendment is staged through the contract rather than left to change tracking, so
                // the same call works for a detached assignment too.
                await roles.UpdateUserRoleAsync(assignment);
                await unitOfWork.SaveChangesAsync();
            }

            using (IServiceScope reading = _fixture.Services.CreateScope())
            {
                IRoleRepository roles = reading.ServiceProvider.GetRequiredService<IRoleRepository>();

                UserRole? amended = await roles.GetUserRoleAsync(portalId, _fixture.Seed.MemberUserId, roleId);
                amended.Should().NotBeNull();
                amended!.ExpiryDate.Should().Be(expiry);
                amended.IsTrialUsed.Should().BeTrue();
            }

            using (IServiceScope removing = _fixture.Services.CreateScope())
            {
                IRoleRepository roles = removing.ServiceProvider.GetRequiredService<IRoleRepository>();
                IUnitOfWork unitOfWork = removing.ServiceProvider.GetRequiredService<IUnitOfWork>();

                // Removal is by the account-and-role pair, which is how the legacy DeleteUserRole
                // identified the row - no prior read and no portal argument.
                await roles.DeleteUserRoleAsync(_fixture.Seed.MemberUserId, roleId);
                await unitOfWork.SaveChangesAsync();
            }

            using IServiceScope after = _fixture.Services.CreateScope();
            IRoleRepository remaining = after.ServiceProvider.GetRequiredService<IRoleRepository>();

            (await remaining.GetUserRoleAsync(portalId, _fixture.Seed.MemberUserId, roleId)).Should().BeNull();
            (await remaining.GetByIdAsync(roleId, portalId)).Should()
                .NotBeNull("removing a member does not remove the role");

            // Removing an assignment that is already gone is not an error, exactly as the legacy
            // key-matched delete affected no row and reported nothing.
            await remaining.DeleteUserRoleAsync(_fixture.Seed.MemberUserId, roleId);
        }
        finally
        {
            await RemoveRoleAsync(roleId);
            await RemovePortalAsync(portalId);
        }
    }

    /// <summary>
    /// The two assignment write members stage both validity bounds exactly as they are handed them: a null
    /// stays a null, a real instant is stored verbatim, and the legacy absent-date marker is refused by the
    /// store rather than quietly reinterpreted.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// THE THIRD ASSERTION IS THE LOAD-BEARING ONE. Both columns are <c>datetime</c>, whose range begins at
    /// 1753-01-01, so 0001-01-01 is not merely absent from them but UNSTORABLE. Proving that the store
    /// refuses it is what makes the Application-layer translation demonstrably necessary rather than merely
    /// tidy, and it proves that nothing between the entity and the column silently hides the marker - which
    /// is precisely the guarantee <c>UserRoleConfiguration</c> claims by installing no value conversion.
    /// </remarks>
    [Fact]
    public async Task Assignment_StagesBothBoundsExactlyAsSupplied()
    {
        int portalId = await CreatePortalAsync();
        int roleId = await CreateRoleAsync(portalId, FormattableString.Invariant($"Marker {Suffix()}"));
        DateTime marker = DateTime.MinValue.AddHours(5);
        DateTime realExpiry = new(2027, 6, 1, 0, 0, 0, DateTimeKind.Utc);

        try
        {
            using (IServiceScope adding = _fixture.Services.CreateScope())
            {
                IRoleRepository roles = adding.ServiceProvider.GetRequiredService<IRoleRepository>();
                IUnitOfWork unitOfWork = adding.ServiceProvider.GetRequiredService<IUnitOfWork>();

                // "No bound" is a null and nothing else - which is what every production caller supplies,
                // because RoleService has already read a submitted marker as absence by this point.
                await roles.AddUserRoleAsync(new UserRole
                {
                    UserId = _fixture.Seed.MemberUserId,
                    RoleId = roleId,
                    EffectiveDate = null,
                    ExpiryDate = null,
                });

                await unitOfWork.SaveChangesAsync();
            }

            (await CountNullBoundsAsync(roleId)).Should().Be(
                1,
                "an assignment staged with two null bounds is stored with both columns null");

            using (IServiceScope amending = _fixture.Services.CreateScope())
            {
                IRoleRepository roles = amending.ServiceProvider.GetRequiredService<IRoleRepository>();
                IUnitOfWork unitOfWork = amending.ServiceProvider.GetRequiredService<IUnitOfWork>();

                UserRole? assignment = await roles.GetUserRoleAsync(portalId, _fixture.Seed.MemberUserId, roleId);
                assignment.Should().NotBeNull();

                // A real bound, so the null that follows cannot be mistaken for the value simply never
                // having changed.
                assignment!.ExpiryDate = realExpiry;
                await roles.UpdateUserRoleAsync(assignment);
                await unitOfWork.SaveChangesAsync();
            }

            (await CountNullBoundsAsync(roleId)).Should().Be(0, "the expiry now holds a real instant");

            using (IServiceScope reading = _fixture.Services.CreateScope())
            {
                IRoleRepository reader = reading.ServiceProvider.GetRequiredService<IRoleRepository>();

                UserRole? stored = await reader.GetUserRoleAsync(portalId, _fixture.Seed.MemberUserId, roleId);
                stored.Should().NotBeNull();
                stored!.ExpiryDate.Should().Be(realExpiry, "the stated bound is persisted verbatim");
                stored.EffectiveDate.Should().BeNull("and the bound that was never set is still absent");
            }

            using (IServiceScope clearing = _fixture.Services.CreateScope())
            {
                IRoleRepository roles = clearing.ServiceProvider.GetRequiredService<IRoleRepository>();
                IUnitOfWork unitOfWork = clearing.ServiceProvider.GetRequiredService<IUnitOfWork>();

                UserRole? assignment = await roles.GetUserRoleAsync(portalId, _fixture.Seed.MemberUserId, roleId);
                assignment.Should().NotBeNull();

                assignment!.ExpiryDate = null;
                await roles.UpdateUserRoleAsync(assignment);
                await unitOfWork.SaveChangesAsync();
            }

            (await CountNullBoundsAsync(roleId)).Should().Be(
                1,
                "and an amendment back to a null clears the column");

            using (IServiceScope refusing = _fixture.Services.CreateScope())
            {
                IRoleRepository roles = refusing.ServiceProvider.GetRequiredService<IRoleRepository>();
                IUnitOfWork unitOfWork = refusing.ServiceProvider.GetRequiredService<IUnitOfWork>();

                UserRole? assignment = await roles.GetUserRoleAsync(portalId, _fixture.Seed.MemberUserId, roleId);
                assignment.Should().NotBeNull();

                assignment!.ExpiryDate = marker;
                await roles.UpdateUserRoleAsync(assignment);

                // Refused by the STORE, loudly, rather than reinterpreted anywhere on the way to it. This is
                // the assertion that makes the Application-layer translation necessary.
                Func<Task> save = () => unitOfWork.SaveChangesAsync();
                await save.Should().ThrowAsync<DbUpdateException>(
                    "the datetime column cannot hold 0001-01-01 and nothing between the entity and the "
                    + "column translates it");
            }

            using IServiceScope after = _fixture.Services.CreateScope();
            IRoleRepository unchanged = after.ServiceProvider.GetRequiredService<IRoleRepository>();

            UserRole? survivor = await unchanged.GetUserRoleAsync(portalId, _fixture.Seed.MemberUserId, roleId);
            survivor.Should().NotBeNull();
            survivor!.EffectiveDate.Should().BeNull("the refused save changed nothing");
            survivor.ExpiryDate.Should().BeNull();
            survivor.GetStatus(DateTime.UtcNow).Should().Be(
                RoleStatus.Active,
                "so the Domain classifies an unbounded membership as in force without recognising a sentinel");
        }
        finally
        {
            await RemoveAssignmentsAsync(roleId);
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
    /// This is how a role that automatically assigns itself enrols the existing membership at the moment it
    /// is created. The identifier is not known until the save happens, and it may turn out to be zero,
    /// which is indistinguishable from an unset value.
    /// </remarks>
    [Fact]
    public async Task AddAsync_ThroughTheNavigation_SavesARoleAndItsFirstMemberTogether()
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

                // Both stagings return a bare task: neither yields the generated key, which is exactly what
                // lets the role and its first member commit as one unit even though the key is not known
                // until they do.
                await roles.AddAsync(role);
                await roles.AddUserRoleAsync(new UserRole { UserId = _fixture.Seed.MemberUserId, Role = role });

                await unitOfWork.SaveChangesAsync();

                roleId = role.RoleId;
            }

            using IServiceScope reading = _fixture.Services.CreateScope();
            IRoleRepository confirming = reading.ServiceProvider.GetRequiredService<IRoleRepository>();

            (await confirming.GetByIdAsync(roleId, portalId)).Should().NotBeNull();

            UserRole? assignment = await confirming.GetUserRoleAsync(portalId, _fixture.Seed.MemberUserId, roleId);
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
    /// The cascade is declared in the schema rather than performed by the application. Leaving the
    /// membership rows behind would leave assignments naming a role that no longer exists, and the
    /// permission evaluation resolves role names by joining through exactly those rows.
    /// </remarks>
    [Fact]
    public async Task DeleteAsync_TakesTheRolesMembershipsWithIt()
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

            (await roles.GetByIdAsync(roleId, portalId)).Should().BeNull();
            (await CountAssignmentsAsync(roleId)).Should().Be(0);
            (await roles.GetUserRoleAsync(portalId, _fixture.Seed.MemberUserId, roleId)).Should().BeNull();

            // The account itself survives; only its membership of the removed role is gone.
            IUserRepository users = scope.ServiceProvider.GetRequiredService<IUserRepository>();
            (await users.GetAsync(_fixture.Seed.PortalId, _fixture.Seed.MemberUserId)).Should().NotBeNull();
        }
        finally
        {
            await RemovePortalAsync(portalId);
        }
    }

    /// <summary>
    /// Every paid-membership term is amended through the update member and read back from the store.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// The fee is deliberately 999.99, the largest value a <c>decimal(5, 2)</c> column could hold.
    /// </remarks>
    [Fact]
    public async Task UpdateAsync_AmendsEveryPaidMembershipTerm()
    {
        const decimal serviceFee = 999.99m;
        const decimal trialFee = 4.95m;
        const int billingPeriod = 3;
        const int trialPeriod = 14;
        const string rsvpCode = "JOIN-2030";
        const string iconFile = "subscription.gif";
        const string amendedDescription = "Amended by the persistence role suite.";

        int portalId = await CreatePortalAsync();
        int roleGroupId = await CreateGroupAsync(portalId, FormattableString.Invariant($"Paid {Suffix()}"));
        int roleId = await CreateRoleAsync(portalId, FormattableString.Invariant($"Subscription {Suffix()}"));

        try
        {
            using (IServiceScope amending = _fixture.Services.CreateScope())
            {
                IRoleRepository roles = amending.ServiceProvider.GetRequiredService<IRoleRepository>();
                IUnitOfWork unitOfWork = amending.ServiceProvider.GetRequiredService<IUnitOfWork>();

                Role? role = await roles.GetByIdAsync(roleId, portalId);
                role.Should().NotBeNull();
                role!.RoleGroupId.Should().BeNull("the role was created outside any group");

                role.Description = amendedDescription;
                role.RoleGroupId = roleGroupId;
                role.ServiceFee = serviceFee;
                role.BillingPeriod = billingPeriod;
                role.BillingFrequency = BillingFrequency.Month;
                role.TrialFee = trialFee;
                role.TrialPeriod = trialPeriod;
                role.TrialFrequency = BillingFrequency.Day;
                role.IsPublic = true;
                role.AutoAssignment = true;
                role.RsvpCode = rsvpCode;
                role.IconFile = iconFile;

                // Staged through the contract rather than left to change tracking, so the call behaves
                // the same way for a detached role rebuilt from a request as for this tracked one.
                await roles.UpdateAsync(role);

                int affected = await unitOfWork.SaveChangesAsync();
                affected.Should().BePositive("the unit of work reports the rows the amendment reached");
            }

            using IServiceScope reading = _fixture.Services.CreateScope();
            IRoleRepository reader = reading.ServiceProvider.GetRequiredService<IRoleRepository>();

            Role? amended = await reader.GetByIdAsync(roleId, portalId);

            amended.Should().NotBeNull();
            amended!.Description.Should().Be(amendedDescription);
            amended.ServiceFee.Should().Be(serviceFee, "the ceiling of a two-decimal-place column round-trips exactly");
            amended.TrialFee.Should().Be(trialFee);
            amended.BillingPeriod.Should().Be(billingPeriod);
            amended.TrialPeriod.Should().Be(trialPeriod);
            amended.BillingFrequency.Should().Be(BillingFrequency.Month);
            amended.TrialFrequency.Should().Be(BillingFrequency.Day);
            amended.IsPublic.Should().BeTrue();
            amended.AutoAssignment.Should().BeTrue();
            amended.RsvpCode.Should().Be(rsvpCode);
            amended.IconFile.Should().Be(iconFile);

            // The reassignment is visible from both ends: the role carries the key and the group's own
            // listing now admits it.
            amended.RoleGroupId.Should().Be(roleGroupId);
            amended.RoleGroup.Should().NotBeNull("the read includes the group the role belongs to");
            amended.RoleGroup!.RoleGroupId.Should().Be(roleGroupId);

            (await reader.GetRolesByGroupAsync(roleGroupId, portalId))
                .Select(role => role.RoleId)
                .Should().Equal(roleId);
        }
        finally
        {
            await RemoveRoleAsync(roleId);
            await RemoveGroupAsync(roleGroupId);
            await RemovePortalAsync(portalId);
        }
    }

    /// <summary>
    /// Amending a priced role back to unpriced clears the optional terms rather than leaving the previous
    /// figures behind.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// This is the half of the update path that a round-trip test alone never reaches. Writing a value and
    /// reading it back proves the column is bound; writing an absence over a value proves the binding
    /// carries absence too, which is what a role reverting from paid to free requires.
    /// </remarks>
    [Fact]
    public async Task UpdateAsync_ClearsTheOptionalTermsBackToAbsent()
    {
        int portalId = await CreatePortalAsync();
        int roleGroupId = await CreateGroupAsync(portalId, FormattableString.Invariant($"Clearing {Suffix()}"));
        int roleId = await CreateRoleAsync(
            portalId,
            FormattableString.Invariant($"Reverting {Suffix()}"),
            roleGroupId: roleGroupId);

        try
        {
            using (IServiceScope pricing = _fixture.Services.CreateScope())
            {
                IRoleRepository roles = pricing.ServiceProvider.GetRequiredService<IRoleRepository>();
                IUnitOfWork unitOfWork = pricing.ServiceProvider.GetRequiredService<IUnitOfWork>();

                Role? role = await roles.GetByIdAsync(roleId, portalId);
                role.Should().NotBeNull();

                // Priced first, so the clearing that follows genuinely has something to overwrite and a
                // null cannot be mistaken for a column that was never written.
                role!.ServiceFee = 19.99m;
                role.BillingPeriod = 1;
                role.BillingFrequency = BillingFrequency.Year;
                role.TrialFee = 1.5m;
                role.TrialPeriod = 7;
                role.TrialFrequency = BillingFrequency.Week;
                role.RsvpCode = "TEMPORARY";
                role.IconFile = "temporary.gif";

                await roles.UpdateAsync(role);
                await unitOfWork.SaveChangesAsync();
            }

            using (IServiceScope clearing = _fixture.Services.CreateScope())
            {
                IRoleRepository roles = clearing.ServiceProvider.GetRequiredService<IRoleRepository>();
                IUnitOfWork unitOfWork = clearing.ServiceProvider.GetRequiredService<IUnitOfWork>();

                Role? role = await roles.GetByIdAsync(roleId, portalId);
                role.Should().NotBeNull();
                role!.BillingFrequency.Should().Be(BillingFrequency.Year, "the priced state is in place");

                role.RoleGroupId = null;
                role.BillingPeriod = null;
                role.BillingFrequency = null;
                role.TrialFee = null;
                role.TrialPeriod = null;
                role.TrialFrequency = null;
                role.RsvpCode = null;
                role.IconFile = null;

                await roles.UpdateAsync(role);
                await unitOfWork.SaveChangesAsync();
            }

            using IServiceScope reading = _fixture.Services.CreateScope();
            IRoleRepository reader = reading.ServiceProvider.GetRequiredService<IRoleRepository>();

            Role? cleared = await reader.GetByIdAsync(roleId, portalId);

            cleared.Should().NotBeNull();
            cleared!.RoleGroupId.Should().BeNull();
            cleared.RoleGroup.Should().BeNull("no group is loaded for a role that belongs to none");
            cleared.BillingPeriod.Should().BeNull("BillingPeriod is int? - the schema settled the type");
            cleared.BillingFrequency.Should().BeNull("an absent code is not the member whose code is 'N'");
            cleared.TrialFee.Should().BeNull();
            cleared.TrialPeriod.Should().BeNull();
            cleared.TrialFrequency.Should().BeNull();
            cleared.RsvpCode.Should().BeNull();
            cleared.IconFile.Should().BeNull();

            // Read straight out of the store as well, because a null property could otherwise be a
            // converter answering null for a column that still holds its old character.
            (await CountClearedTermsAsync(roleId)).Should().Be(
                1,
                "the columns themselves are null rather than carrying the figures they held before");

            // The group survives the role leaving it, and no longer lists the role.
            (await reader.GetRoleGroupAsync(portalId, roleGroupId)).Should().NotBeNull();
            (await reader.GetRolesByGroupAsync(roleGroupId, portalId)).Should().BeEmpty();
        }
        finally
        {
            await RemoveRoleAsync(roleId);
            await RemoveGroupAsync(roleGroupId);
            await RemovePortalAsync(portalId);
        }
    }

    /// <summary>A role group is renamed and redescribed through its own update member.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task UpdateRoleGroupAsync_RenamesTheGroupWithoutDisturbingItsRoles()
    {
        int portalId = await CreatePortalAsync();
        string marker = Suffix();
        int roleGroupId = await CreateGroupAsync(portalId, FormattableString.Invariant($"Original {marker}"));
        int roleId = await CreateRoleAsync(
            portalId,
            FormattableString.Invariant($"Grouped {marker}"),
            roleGroupId: roleGroupId);
        string renamed = FormattableString.Invariant($"Renamed {marker}");

        try
        {
            using (IServiceScope amending = _fixture.Services.CreateScope())
            {
                IRoleRepository roles = amending.ServiceProvider.GetRequiredService<IRoleRepository>();
                IUnitOfWork unitOfWork = amending.ServiceProvider.GetRequiredService<IUnitOfWork>();

                RoleGroup? group = await roles.GetRoleGroupAsync(portalId, roleGroupId);
                group.Should().NotBeNull();

                group!.RoleGroupName = renamed;
                group.Description = "Amended by the persistence role suite.";

                await roles.UpdateRoleGroupAsync(group);

                int affected = await unitOfWork.SaveChangesAsync();
                affected.Should().BePositive();
            }

            using IServiceScope reading = _fixture.Services.CreateScope();
            IRoleRepository reader = reading.ServiceProvider.GetRequiredService<IRoleRepository>();

            RoleGroup? amended = await reader.GetRoleGroupAsync(portalId, roleGroupId);

            amended.Should().NotBeNull();
            amended!.RoleGroupName.Should().Be(renamed);
            amended.Description.Should().Be("Amended by the persistence role suite.");
            amended.PortalId.Should().Be(portalId);

            (await reader.GetRoleGroupsAsync(portalId))
                .Select(group => group.RoleGroupName)
                .Should().Equal(renamed);

            // The membership is untouched by the rename, from both directions.
            (await reader.GetRolesByGroupAsync(roleGroupId, portalId))
                .Select(role => role.RoleId)
                .Should().Equal(roleId);

            Role? grouped = await reader.GetByIdAsync(roleId, portalId);
            grouped.Should().NotBeNull();
            grouped!.RoleGroup.Should().NotBeNull();
            grouped.RoleGroup!.RoleGroupName.Should().Be(renamed);
        }
        finally
        {
            await RemoveRoleAsync(roleId);
            await RemoveGroupAsync(roleGroupId);
            await RemovePortalAsync(portalId);
        }
    }

    /// <summary>
    /// Each of the six legacy billing codes survives a write and a read through the repository, and reaches
    /// the column as the single character the legacy engine branched on.
    /// </summary>
    /// <param name="frequency">The member under test.</param>
    /// <param name="storedCode">The character the column must hold for that member.</param>
    /// <returns>A task representing the test.</returns>
    [Theory]
    [Trait("Category", "Integration")]
    [InlineData(BillingFrequency.None, "N")]
    [InlineData(BillingFrequency.OneTime, "O")]
    [InlineData(BillingFrequency.Day, "D")]
    [InlineData(BillingFrequency.Week, "W")]
    [InlineData(BillingFrequency.Month, "M")]
    [InlineData(BillingFrequency.Year, "Y")]
    public async Task BillingCodes_EachOfTheSixRoundTripsAsItsLegacyCharacter(
        BillingFrequency frequency,
        string storedCode)
    {
        int portalId = await CreatePortalAsync();
        int roleId;

        using (IServiceScope writing = _fixture.Services.CreateScope())
        {
            IRoleRepository roles = writing.ServiceProvider.GetRequiredService<IRoleRepository>();
            IUnitOfWork unitOfWork = writing.ServiceProvider.GetRequiredService<IUnitOfWork>();

            Role role = new()
            {
                PortalId = portalId,
                RoleName = FormattableString.Invariant($"Coded {storedCode} {Suffix()}"),
                Description = "Created by the persistence role suite.",
                ServiceFee = 9.99m,
                BillingPeriod = 1,
                BillingFrequency = frequency,
                TrialFee = 0m,
                TrialPeriod = 1,
                TrialFrequency = frequency,
            };

            await roles.AddAsync(role);
            await unitOfWork.SaveChangesAsync();

            roleId = role.RoleId;
        }

        try
        {
            (await ReadStoredBillingFrequencyAsync(roleId)).Should().Be(storedCode);
            (await ReadStoredTrialFrequencyAsync(roleId)).Should().Be(storedCode);

            using IServiceScope reading = _fixture.Services.CreateScope();
            IRoleRepository reader = reading.ServiceProvider.GetRequiredService<IRoleRepository>();

            Role? stored = await reader.GetByIdAsync(roleId, portalId);

            stored.Should().NotBeNull();
            stored!.BillingFrequency.Should().Be(frequency);
            stored.TrialFrequency.Should().Be(frequency);

            // The code point of the member IS the stored character, which is what keeps the two
            // representations from drifting apart if a member is ever reordered.
            ((char)frequency).ToString().Should().Be(storedCode);
        }
        finally
        {
            await RemoveRoleAsync(roleId);
            await RemovePortalAsync(portalId);
        }
    }

    /// <summary>
    /// A stored billing code outside the documented set is read without throwing, which is what keeps the
    /// roles every DotNetNuke installation ships with readable.
    /// </summary>
    /// <param name="storedCode">A single character an existing installation is known to hold.</param>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// THIS IS THE MOST CONSEQUENTIAL ASSERTION IN THIS FILE, and the reason is in the shipped data rather
    /// than in the code. <c>Roles.BillingFrequency</c> is <c>char(1) NULL</c>, which accepts any single
    /// character, and the installation seed in <c>01.00.00.SqlDataProvider</c> takes that literally: the
    /// Administrators role is inserted with <c>BillingFrequency = '4'</c> and the Registered Users role
    /// with <c>'0'</c>.
    /// </remarks>
    [Theory]
    [Trait("Category", "Integration")]
    [InlineData("4")]
    [InlineData("0")]
    public async Task UnrecognisedStoredBillingCode_IsReadWithoutThrowing(string storedCode)
    {
        int portalId = await CreatePortalAsync();
        int roleId = await CreateRoleAsync(
            portalId,
            FormattableString.Invariant($"Legacy Coded {Suffix()}"));

        try
        {
            await OverwriteStoredFrequenciesAsync(roleId, storedCode);

            (await ReadStoredBillingFrequencyAsync(roleId)).Should().Be(
                storedCode,
                "the row now holds exactly what a shipped installation holds");

            using IServiceScope reading = _fixture.Services.CreateScope();
            IRoleRepository reader = reading.ServiceProvider.GetRequiredService<IRoleRepository>();

            Func<Task> readingTheRole = async () =>
            {
                _ = await reader.GetByIdAsync(roleId, portalId);
                _ = await reader.GetByPortalIdAsync(portalId);
                _ = await reader.GetAllAsync();
            };

            await readingTheRole.Should().NotThrowAsync(
                "a role carrying an undocumented frequency code must remain readable");

            var carried = (BillingFrequency)storedCode[0];

            Role? single = await reader.GetByIdAsync(roleId, portalId);
            single.Should().NotBeNull();
            single!.BillingFrequency.Should().Be(
                carried,
                "an unrecognised code is carried as the character the row holds, not normalised away");
            single.TrialFrequency.Should().Be(carried);
            Enum.IsDefined(single.BillingFrequency!.Value).Should().BeFalse(
                "the shipped codes really are outside the declared vocabulary");

            // The listing path materialises the same row through the same conversion, so it must agree.
            (await reader.GetByPortalIdAsync(portalId))
                .Where(role => role.RoleId == roleId)
                .Select(role => role.BillingFrequency)
                .Should().Equal(carried);

            // The resolution is deliberately NOT null.
            (await ReadStoredBillingFrequencyAsync(roleId)).Should().Be(
                storedCode,
                "reading the row does not rewrite it");
        }
        finally
        {
            await RemoveRoleAsync(roleId);
            await RemovePortalAsync(portalId);
        }
    }

    /// <summary>
    /// An edit to something else entirely leaves a stored frequency character the enumeration does not
    /// declare exactly as the installation stored it.
    /// </summary>
    /// <param name="storedBilling">The billing character to plant.</param>
    /// <param name="storedTrial">The trial character to plant, deliberately different from the billing one.</param>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// The two columns are planted with DIFFERENT characters on purpose. A single shared character would
    /// let a conversion that read one column and wrote both pass, and the two columns are the same store
    /// type over the same vocabulary bound by the same shared converter instance - so proving they move
    /// independently is what proves neither is being written from the other.
    /// </remarks>
    [Theory]
    [Trait("Category", "Integration")]
    [InlineData("4", "0")]
    [InlineData("0", "4")]
    public async Task UnrecognisedStoredBillingCode_SurvivesAnUnrelatedUpdate(
        string storedBilling,
        string storedTrial)
    {
        int portalId = await CreatePortalAsync();
        int roleId = await CreateRoleAsync(
            portalId,
            FormattableString.Invariant($"Preserved Coded {Suffix()}"));

        try
        {
            int affected = await _fixture.Database.ExecuteAsync(
                "UPDATE [dbo].[Roles] SET [BillingFrequency] = @billing, [TrialFrequency] = @trial "
                + "WHERE [RoleID] = @roleId",
                new Dictionary<string, object?>
                {
                    ["billing"] = storedBilling,
                    ["trial"] = storedTrial,
                    ["roleId"] = roleId,
                });

            affected.Should().Be(1, "the role this test created is the only row addressed");

            string description = FormattableString.Invariant($"Unrelated edit {Suffix()}");

            using (IServiceScope editing = _fixture.Services.CreateScope())
            {
                IRoleRepository roles = editing.ServiceProvider.GetRequiredService<IRoleRepository>();
                IUnitOfWork unitOfWork = editing.ServiceProvider.GetRequiredService<IUnitOfWork>();

                Role? subject = await roles.GetByIdAsync(roleId, portalId);
                subject.Should().NotBeNull();

                subject!.BillingFrequency.Should().Be(
                    (BillingFrequency)storedBilling[0],
                    "the read carries the stored character rather than normalising it");
                subject.TrialFrequency.Should().Be((BillingFrequency)storedTrial[0]);

                // The ONLY change. Everything else about the role is left exactly as it was read.
                subject.Description = description;

                await roles.UpdateAsync(subject);
                await unitOfWork.SaveChangesAsync();
            }

            (await ReadStoredBillingFrequencyAsync(roleId)).Should().Be(
                storedBilling,
                "an edit to the description must not rewrite the billing frequency");
            (await ReadStoredTrialFrequencyAsync(roleId)).Should().Be(
                storedTrial,
                "nor the trial frequency, and the two must not have been written from one another");

            using IServiceScope verifying = _fixture.Services.CreateScope();
            IRoleRepository verifier = verifying.ServiceProvider.GetRequiredService<IRoleRepository>();

            Role? reread = await verifier.GetByIdAsync(roleId, portalId);
            reread.Should().NotBeNull();
            reread!.Description.Should().Be(description, "the edit the caller did ask for was applied");
        }
        finally
        {
            await RemoveRoleAsync(roleId);
            await RemovePortalAsync(portalId);
        }
    }

    /// <summary>
    /// A detached role whose stored frequency characters are outside the vocabulary keeps them through an
    /// update staged from a rebuilt instance.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// The companion to the case above, and the harder half. A TRACKED role writes only the columns the
    /// caller changed, so preservation there follows from the change tracker as much as from the
    /// conversion.
    /// </remarks>
    [Fact]
    public async Task UnrecognisedStoredBillingCode_SurvivesADetachedUpdate()
    {
        int portalId = await CreatePortalAsync();
        int roleId = await CreateRoleAsync(
            portalId,
            FormattableString.Invariant($"Detached Coded {Suffix()}"));

        try
        {
            await _fixture.Database.ExecuteAsync(
                "UPDATE [dbo].[Roles] SET [BillingFrequency] = '4', [TrialFrequency] = '0' "
                + "WHERE [RoleID] = @roleId",
                new Dictionary<string, object?> { ["roleId"] = roleId });

            Role detached;
            using (IServiceScope reading = _fixture.Services.CreateScope())
            {
                IRoleRepository roles = reading.ServiceProvider.GetRequiredService<IRoleRepository>();
                Role? read = await roles.GetByIdAsync(roleId, portalId);

                read.Should().NotBeNull();
                detached = read!;
            }

            string description = FormattableString.Invariant($"Detached edit {Suffix()}");
            detached.Description = description;

            using (IServiceScope writing = _fixture.Services.CreateScope())
            {
                IRoleRepository roles = writing.ServiceProvider.GetRequiredService<IRoleRepository>();
                IUnitOfWork unitOfWork = writing.ServiceProvider.GetRequiredService<IUnitOfWork>();

                await roles.UpdateAsync(detached);
                await unitOfWork.SaveChangesAsync();
            }

            (await ReadStoredBillingFrequencyAsync(roleId)).Should().Be(
                "4",
                "a full-row write must reproduce the character it read, not a substitute for it");
            (await ReadStoredTrialFrequencyAsync(roleId)).Should().Be("0");

            using IServiceScope verifying = _fixture.Services.CreateScope();
            IRoleRepository verifier = verifying.ServiceProvider.GetRequiredService<IRoleRepository>();

            (await verifier.GetByIdAsync(roleId, portalId))!.Description.Should().Be(description);
        }
        finally
        {
            await RemoveRoleAsync(roleId);
            await RemovePortalAsync(portalId);
        }
    }

    /// <summary>
    /// A staged role is invisible until the unit of work commits, and its generated key is only meaningful
    /// afterwards.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// EVERY LEGACY INSERT RETURNED THE GENERATED KEY; NONE OF THESE DOES. Each legacy procedure ended in
    /// <c>SCOPE_IDENTITY()</c> and its provider member was declared <c>As Integer</c> - <c>AddRole</c>,
    /// <c>AddRoleGroup</c> and <c>AddUserRole</c> alike - so the caller received the key at the moment of
    /// the call and the call therefore had to be its own transaction.
    /// </remarks>
    [Fact]
    public async Task AddAsync_StagesTheRoleAndTheKeyArrivesOnlyWithTheCommit()
    {
        int portalId = await CreatePortalAsync();
        string roleName = FormattableString.Invariant($"Staged {Suffix()}");
        int roleId = -1;

        try
        {
            using (IServiceScope staging = _fixture.Services.CreateScope())
            {
                IRoleRepository roles = staging.ServiceProvider.GetRequiredService<IRoleRepository>();
                IUnitOfWork unitOfWork = staging.ServiceProvider.GetRequiredService<IUnitOfWork>();

                Role role = new()
                {
                    PortalId = portalId,
                    RoleName = roleName,
                    Description = "Created by the persistence role suite.",
                };

                role.IdentityIsPersisted.Should().BeFalse("a freshly constructed entity declares nothing");

                await roles.AddAsync(role);

                role.RoleId.Should().Be(
                    default,
                    "the property still holds its CLR default, which is not evidence of absence here");
                role.IdentityIsPersisted.Should().BeFalse("staging is not persisting");

                // The claim that nothing is persisted yet, made where it can actually be observed: a
                // second scope has its own change tracker and can only see committed rows.
                using (IServiceScope beforeCommit = _fixture.Services.CreateScope())
                {
                    IRoleRepository other = beforeCommit.ServiceProvider.GetRequiredService<IRoleRepository>();

                    (await other.GetByNameAsync(portalId, roleName)).Should().BeNull(
                        "a staged role is not in the store until the unit of work commits");
                }

                int affected = await unitOfWork.SaveChangesAsync();
                affected.Should().Be(1, "one row was staged, so one row was written");

                roleId = role.RoleId;
            }

            using IServiceScope afterCommit = _fixture.Services.CreateScope();
            IRoleRepository reader = afterCommit.ServiceProvider.GetRequiredService<IRoleRepository>();

            Role? committed = await reader.GetByIdAsync(roleId, portalId);

            committed.Should().NotBeNull("the key read off the entity after the commit addresses the row");
            committed!.RoleName.Should().Be(roleName);

            (await reader.GetByNameAsync(portalId, roleName)).Should().NotBeNull();
        }
        finally
        {
            if (roleId >= 0)
            {
                await RemoveRoleAsync(roleId);
            }

            await RemovePortalAsync(portalId);
        }
    }

    /// <summary>
    /// A group, a role inside it and that role's first member are written by one call to the unit of work.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// This is the commit boundary the whole staging arrangement exists for, exercised across all three
    /// tables this contract owns.
    /// </remarks>
    [Fact]
    public async Task OneSaveChanges_CommitsAGroupARoleAndItsFirstMemberTogether()
    {
        int portalId = await CreatePortalAsync();
        string marker = Suffix();
        string groupName = FormattableString.Invariant($"Atomic Group {marker}");
        string roleName = FormattableString.Invariant($"Atomic Role {marker}");
        int roleGroupId = -1;
        int roleId = -1;

        try
        {
            using (IServiceScope staging = _fixture.Services.CreateScope())
            {
                IRoleRepository roles = staging.ServiceProvider.GetRequiredService<IRoleRepository>();
                IUnitOfWork unitOfWork = staging.ServiceProvider.GetRequiredService<IUnitOfWork>();

                RoleGroup group = new()
                {
                    PortalId = portalId,
                    RoleGroupName = groupName,
                    Description = "Created by the persistence role suite.",
                };

                Role role = new()
                {
                    PortalId = portalId,
                    RoleName = roleName,
                    Description = "Created by the persistence role suite.",
                    AutoAssignment = true,
                    RoleGroup = group,
                };

                UserRole firstMember = new()
                {
                    UserId = _fixture.Seed.MemberUserId,
                    Role = role,
                    IsTrialUsed = false,
                };

                await roles.AddRoleGroupAsync(group);
                await roles.AddAsync(role);
                await roles.AddUserRoleAsync(firstMember);

                using (IServiceScope beforeCommit = _fixture.Services.CreateScope())
                {
                    IRoleRepository other = beforeCommit.ServiceProvider.GetRequiredService<IRoleRepository>();

                    (await other.GetRoleGroupsAsync(portalId)).Should().BeEmpty();
                    (await other.GetByNameAsync(portalId, roleName)).Should().BeNull();
                }

                int affected = await unitOfWork.SaveChangesAsync();
                affected.Should().Be(3, "the group, the role and the assignment are one unit of work");

                roleGroupId = group.RoleGroupId;
                roleId = role.RoleId;
            }

            using IServiceScope reading = _fixture.Services.CreateScope();
            IRoleRepository reader = reading.ServiceProvider.GetRequiredService<IRoleRepository>();

            (await reader.GetRoleGroupAsync(portalId, roleGroupId)).Should().NotBeNull();

            Role? committed = await reader.GetByIdAsync(roleId, portalId);
            committed.Should().NotBeNull();
            committed!.RoleGroupId.Should().Be(
                roleGroupId,
                "the store resolved the group key from the navigation");

            (await reader.GetRolesByGroupAsync(roleGroupId, portalId))
                .Select(role => role.RoleId)
                .Should().Equal(roleId);

            UserRole? assignment = await reader.GetUserRoleAsync(
                portalId,
                _fixture.Seed.MemberUserId,
                roleId);

            assignment.Should().NotBeNull();
            assignment!.RoleId.Should().Be(roleId, "and the role key from the other navigation");
        }
        finally
        {
            if (roleId >= 0)
            {
                await RemoveAssignmentsAsync(roleId);
                await RemoveRoleAsync(roleId);
            }

            if (roleGroupId >= 0)
            {
                await RemoveGroupAsync(roleGroupId);
            }

            await RemovePortalAsync(portalId);
        }
    }

    /// <summary>
    /// A membership classifies itself identically before and after the store has held it, at every one of
    /// the three outcomes.
    /// </summary>
    /// <param name="effectiveOffsetDays">
    /// Days from the classification instant to the effective bound, or <see langword="null"/> for no bound.
    /// </param>
    /// <param name="expiryOffsetDays">
    /// Days from the classification instant to the expiry bound, or <see langword="null"/> for no bound.
    /// </param>
    /// <param name="expected">The classification both the staged and the stored membership must report.</param>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// The classification itself is a pure Domain method taking the instant as a parameter, so it needs no
    /// database to exercise.
    /// </remarks>
    [Theory]
    [Trait("Category", "Integration")]
    [InlineData(5, 400, RoleStatus.Pending)]
    [InlineData(null, -30, RoleStatus.Expired)]
    [InlineData(5, -30, RoleStatus.Expired)]
    [InlineData(null, null, RoleStatus.Active)]
    [InlineData(-10, 10, RoleStatus.Active)]
    [InlineData(0, 0, RoleStatus.Active)]
    public async Task GetStatus_ClassifiesTheSameWayBeforeAndAfterPersistence(
        int? effectiveOffsetDays,
        int? expiryOffsetDays,
        RoleStatus expected)
    {
        // A whole-day instant in the future: exactly representable in a datetime column, and late enough
        // that no case below is reinterpreted by the write path's past-effective-date normalisation.
        DateTime asOf = new(2030, 6, 15, 0, 0, 0, DateTimeKind.Utc);
        DateTime? effectiveDate = effectiveOffsetDays is int effectiveOffset
            ? asOf.AddDays(effectiveOffset)
            : null;
        DateTime? expiryDate = expiryOffsetDays is int expiryOffset
            ? asOf.AddDays(expiryOffset)
            : null;

        int portalId = await CreatePortalAsync();
        int roleId = await CreateRoleAsync(portalId, FormattableString.Invariant($"Windowed {Suffix()}"));

        try
        {
            using (IServiceScope staging = _fixture.Services.CreateScope())
            {
                IRoleRepository roles = staging.ServiceProvider.GetRequiredService<IRoleRepository>();
                IUnitOfWork unitOfWork = staging.ServiceProvider.GetRequiredService<IUnitOfWork>();

                UserRole assignment = new()
                {
                    UserId = _fixture.Seed.MemberUserId,
                    RoleId = roleId,
                    EffectiveDate = effectiveDate,
                    ExpiryDate = expiryDate,
                    IsTrialUsed = false,
                };

                await roles.AddUserRoleAsync(assignment);

                // Classified before the store has seen it. The method reads no clock of its own, so the
                // same instant must produce the same answer here as it does after the round-trip.
                assignment.GetStatus(asOf).Should().Be(expected);

                await unitOfWork.SaveChangesAsync();
            }

            using IServiceScope reading = _fixture.Services.CreateScope();
            IRoleRepository reader = reading.ServiceProvider.GetRequiredService<IRoleRepository>();

            UserRole? stored = await reader.GetUserRoleAsync(portalId, _fixture.Seed.MemberUserId, roleId);

            stored.Should().NotBeNull();
            stored!.EffectiveDate.Should().Be(effectiveDate);
            stored.ExpiryDate.Should().Be(expiryDate);
            stored.GetStatus(asOf).Should().Be(expected, "the classification survives the round-trip");

            // Determinism: the same instant twice gives the same answer, and the method leaves no trace on
            // the entity that a second call could observe.
            stored.GetStatus(asOf).Should().Be(stored.GetStatus(asOf));

            // The set of in-force memberships is what the tenant-administration policy grants on, so the
            // Domain's own membership test must agree with the classification for this very assignment.
            UserRole.AnyActiveInRole([stored], roleId, asOf)
                .Should().Be(expected == RoleStatus.Active);
        }
        finally
        {
            await RemoveAssignmentsAsync(roleId);
            await RemoveRoleAsync(roleId);
            await RemovePortalAsync(portalId);
        }
    }

    /// <summary>
    /// The two identity seeds this contract spans disagree, and the disagreement is load-bearing in
    /// opposite directions.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// <c>Roles.RoleID</c> is <c>IDENTITY(0, 1)</c> and <c>UserRoles.UserRoleID</c> is <c>IDENTITY(1,
    /// 1)</c>, so within this one contract zero is a legitimate role key and is not a legitimate assignment
    /// key.
    /// </remarks>
    [Fact]
    public async Task IdentitySeeds_MakeZeroARoleKeyButNeverAnAssignmentKey()
    {
        (await ReadIdentitySeedAsync("dbo.Roles")).Should().Be(0);
        (await ReadIdentitySeedAsync("dbo.RoleGroups")).Should().Be(0);
        (await ReadIdentitySeedAsync("dbo.UserRoles")).Should().Be(1);

        // Observed rather than merely declared: the first role of this database really does hold zero, and
        // it is reachable through the ordinary single-role read.
        _fixture.Seed.AdministratorRoleId.Should().Be(0);

        using IServiceScope scope = _fixture.Services.CreateScope();
        IRoleRepository roles = scope.ServiceProvider.GetRequiredService<IRoleRepository>();

        (await roles.GetByIdAsync(0, _fixture.Seed.PortalId)).Should().NotBeNull(
            "zero addresses the Administrators role rather than meaning 'no role'");

        // And zero is load-bearing across a relationship, not just as a key: the tenant's administrator
        // role column points at it.
        (await ReadAdministratorRoleIdAsync(_fixture.Seed.PortalId)).Should().Be(0);

        (await CountAssignmentsWithZeroKeyAsync()).Should().Be(
            0,
            "no assignment can hold zero, because that column's identity starts at one");

        IReadOnlyList<UserRole> assignments = await roles.GetUserRolesAsync(
            _fixture.Seed.PortalId,
            _fixture.Seed.MemberUserId);

        assignments.Should().NotBeEmpty("the seeded member belongs to the registered-users role");
        assignments.Should().AllSatisfy(assignment => assignment.UserRoleId.Should().BePositive());
    }

    /// <summary>
    /// An empty string stays an empty string and a false flag stays a stored false, neither collapsing into
    /// SQL <c>NULL</c>.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// Evidence that the legacy store is full of such empty strings rather than nulls, so that reading one
    /// back as a null would be a live behavioural change: the seeded tenant row holds <c>HostFee</c> as an
    /// empty string, and the seeded module rows hold their authorised-role lists the same way.
    /// </remarks>
    [Fact]
    public async Task EmptyStringsAndFalseFlagsAreStoredRatherThanCollapsedToNull()
    {
        int portalId = await CreatePortalAsync();
        int roleId;

        using (IServiceScope writing = _fixture.Services.CreateScope())
        {
            IRoleRepository roles = writing.ServiceProvider.GetRequiredService<IRoleRepository>();
            IUnitOfWork unitOfWork = writing.ServiceProvider.GetRequiredService<IUnitOfWork>();

            Role role = new()
            {
                PortalId = portalId,
                RoleName = FormattableString.Invariant($"Emptied {Suffix()}"),
                Description = string.Empty,
                RsvpCode = string.Empty,
                IconFile = string.Empty,
                IsPublic = false,
                AutoAssignment = false,
            };

            await roles.AddAsync(role);
            await unitOfWork.SaveChangesAsync();

            roleId = role.RoleId;
        }

        try
        {
            (await CountEmptyTextAsync(roleId)).Should().Be(
                1,
                "the three text columns hold empty strings rather than nulls");
            (await CountStoredFalseFlagsAsync(roleId)).Should().Be(
                1,
                "and the two flag columns hold a stored false rather than a null");

            using (IServiceScope reading = _fixture.Services.CreateScope())
            {
                IRoleRepository reader = reading.ServiceProvider.GetRequiredService<IRoleRepository>();

                Role? stored = await reader.GetByIdAsync(roleId, portalId);

                stored.Should().NotBeNull();
                stored!.Description.Should().BeEmpty("and comes back as an empty string, not as absence");
                stored.RsvpCode.Should().BeEmpty();
                stored.IconFile.Should().BeEmpty();
                stored.IsPublic.Should().BeFalse();
                stored.AutoAssignment.Should().BeFalse();
            }

            // The nullable flag on the assignment keeps its null, which is the other half of the trade: the
            // divergence above is only acceptable because absence remains expressible where the schema
            // genuinely permits it.
            using (IServiceScope assigning = _fixture.Services.CreateScope())
            {
                IRoleRepository roles = assigning.ServiceProvider.GetRequiredService<IRoleRepository>();
                IUnitOfWork unitOfWork = assigning.ServiceProvider.GetRequiredService<IUnitOfWork>();

                await roles.AddUserRoleAsync(new UserRole
                {
                    UserId = _fixture.Seed.MemberUserId,
                    RoleId = roleId,
                    IsTrialUsed = null,
                });

                await unitOfWork.SaveChangesAsync();
            }

            (await CountNullTrialFlagsAsync(roleId)).Should().Be(1, "an unknown trial flag stays unknown");

            using IServiceScope confirming = _fixture.Services.CreateScope();
            IRoleRepository reader2 = confirming.ServiceProvider.GetRequiredService<IRoleRepository>();

            UserRole? assignment = await reader2.GetUserRoleAsync(
                portalId,
                _fixture.Seed.MemberUserId,
                roleId);

            assignment.Should().NotBeNull();
            assignment!.IsTrialUsed.Should().BeNull("bool? keeps the distinction the legacy layer lost");
        }
        finally
        {
            await RemoveAssignmentsAsync(roleId);
            await RemoveRoleAsync(roleId);
            await RemovePortalAsync(portalId);
        }
    }

    /// <summary>
    /// A role belonging to no tenant is refused by the store, and the refusal leaves the tenant's listing
    /// exactly as it was.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// <strong>What is asserted here instead is a repository property rather than a schema one.</strong>
    /// That the column refuses the row is proved by <c>LegacySchemaFidelityTests</c>
    /// <c>Roles_RefusesARoleThatBelongsToNoPortal</c>; what this adds is that the refusal is CLEAN - the
    /// unit of work leaves nothing behind, and a subsequent read through a fresh scope sees precisely the
    /// roles that existed before the attempt.
    /// </remarks>
    [Fact]
    public async Task GetByPortalIdAsync_IsUnaffectedByARefusedRoleThatBelongsToNoTenant()
    {
        int portalId = await CreatePortalAsync();
        string marker = Suffix();
        int owned = await CreateRoleAsync(portalId, FormattableString.Invariant($"Owned {marker}"));

        try
        {
            IReadOnlyList<int> before;

            using (IServiceScope reading = _fixture.Services.CreateScope())
            {
                IRoleRepository reader = reading.ServiceProvider.GetRequiredService<IRoleRepository>();
                before = (await reader.GetByPortalIdAsync(portalId)).Select(role => role.RoleId).ToList();
            }

            before.Should().Contain(owned);

            string refusedName = FormattableString.Invariant($"Tenant Less {marker}");

            using (IServiceScope writing = _fixture.Services.CreateScope())
            {
                IRoleRepository roles = writing.ServiceProvider.GetRequiredService<IRoleRepository>();
                IUnitOfWork unitOfWork = writing.ServiceProvider.GetRequiredService<IUnitOfWork>();

                await roles.AddAsync(new Role
                {
                    PortalId = null,
                    RoleName = refusedName,
                    Description = "Created by the persistence role suite; the store must refuse it.",
                });

                Func<Task> write = async () => await unitOfWork.SaveChangesAsync();

                await write.Should().ThrowAsync<DbUpdateException>(
                    "the terminal column is NOT NULL, so a role with no owning tenant is a row no "
                    + "installation can hold");
            }

            using IServiceScope after = _fixture.Services.CreateScope();
            IRoleRepository afterReader = after.ServiceProvider.GetRequiredService<IRoleRepository>();

            (await afterReader.GetByPortalIdAsync(portalId)).Select(role => role.RoleId)
                .Should().Equal(before, "a refused write changes nothing that a later read can see");

            (await afterReader.GetByNameAsync(portalId, refusedName)).Should().BeNull(
                "and it leaves no row behind under the name it tried to use");
        }
        finally
        {
            await RemoveRoleAsync(owned);
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

        await portals.AddAsync(portal);
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

        Portal? doomed = await portals.GetByIdAsync(portalId);

        if (doomed is not null)
        {
            await portals.DeleteAsync(doomed.PortalId);
            await unitOfWork.SaveChangesAsync();
        }
    }

    /// <summary>Creates a role through the repository.</summary>
    /// <param name="portalId">The owning tenant.</param>
    /// <param name="roleName">The role name, which must be unique within the tenant.</param>
    /// <param name="autoAssignment">Whether new accounts join the role automatically.</param>
    /// <param name="roleGroupId">The group the role belongs to, if any.</param>
    /// <param name="isPublic">Whether the role is offered for subscription.</param>
    /// <returns>The identifier the store assigned.</returns>
    private async Task<int> CreateRoleAsync(
        int portalId,
        string roleName,
        bool autoAssignment = false,
        int? roleGroupId = null,
        bool isPublic = false)
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
            IsPublic = isPublic,
            RoleGroupId = roleGroupId,
        };

        await roles.AddAsync(role);
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

        // DeleteAsync carries the key alone, exactly as the legacy DeleteRole(RoleId) did, and is a
        // no-op when no such role exists, so no read is needed first.
        await roles.DeleteAsync(roleId);
        await unitOfWork.SaveChangesAsync();
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

        await roles.AddRoleGroupAsync(group);
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

        await roles.DeleteRoleGroupAsync(roleGroupId);
        await unitOfWork.SaveChangesAsync();
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

        await roles.AddUserRoleAsync(new UserRole
        {
            UserId = userId,
            RoleId = roleId,
            IsTrialUsed = false,
        });

        await unitOfWork.SaveChangesAsync();
    }

    /// <summary>Counts the membership rows of a role whose two bounds are both SQL <c>NULL</c>.</summary>
    /// <param name="roleId">The role to count.</param>
    /// <returns>The number of unbounded membership rows.</returns>
    private Task<int> CountNullBoundsAsync(int roleId)
    {
        return _fixture.Database.ScalarAsync<int>(
            "SELECT COUNT(*) FROM [dbo].[UserRoles] "
            + "WHERE [RoleID] = @roleId AND [EffectiveDate] IS NULL AND [ExpiryDate] IS NULL",
            new Dictionary<string, object?> { ["roleId"] = roleId });
    }

    /// <summary>Removes every membership row of a role.</summary>
    /// <param name="roleId">The role to clear.</param>
    /// <returns>A task that completes once the rows are gone.</returns>
    private async Task RemoveAssignmentsAsync(int roleId)
    {
        _ = await _fixture.Database.ExecuteAsync(
            "DELETE FROM [dbo].[UserRoles] WHERE [RoleID] = @roleId",
            new Dictionary<string, object?> { ["roleId"] = roleId });
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

    /// <summary>Reads the billing frequency character a role row holds.</summary>
    /// <param name="roleId">The role to read.</param>
    /// <returns>The stored character.</returns>
    /// <remarks>
    /// Deliberately a separate member from its trial counterpart rather than one taking a column name, so
    /// that no column identifier is ever composed into a statement.
    /// </remarks>
    private Task<string> ReadStoredBillingFrequencyAsync(int roleId)
    {
        return _fixture.Database.ScalarAsync<string>(
            "SELECT [BillingFrequency] FROM [dbo].[Roles] WHERE [RoleID] = @roleId",
            new Dictionary<string, object?> { ["roleId"] = roleId });
    }

    /// <summary>Reads the trial frequency character a role row holds.</summary>
    /// <param name="roleId">The role to read.</param>
    /// <returns>The stored character.</returns>
    private Task<string> ReadStoredTrialFrequencyAsync(int roleId)
    {
        return _fixture.Database.ScalarAsync<string>(
            "SELECT [TrialFrequency] FROM [dbo].[Roles] WHERE [RoleID] = @roleId",
            new Dictionary<string, object?> { ["roleId"] = roleId });
    }

    /// <summary>
    /// Places a raw character in both frequency columns of a role, bypassing the entity conversion.
    /// </summary>
    /// <param name="roleId">The role to amend.</param>
    /// <param name="storedCode">The single character to store.</param>
    /// <returns>A task that completes once the row is amended.</returns>
    /// <remarks>
    /// No code path in the target can write a character outside the documented set, which is exactly why a
    /// statement is used here: the shipped installation data contains such rows, so the read path has to
    /// cope with one even though the write path will never produce one. This amends a data row of a role
    /// this test created and owns; it alters no schema.
    /// </remarks>
    private async Task OverwriteStoredFrequenciesAsync(int roleId, string storedCode)
    {
        int affected = await _fixture.Database.ExecuteAsync(
            "UPDATE [dbo].[Roles] SET [BillingFrequency] = @storedCode, [TrialFrequency] = @storedCode "
            + "WHERE [RoleID] = @roleId",
            new Dictionary<string, object?> { ["storedCode"] = storedCode, ["roleId"] = roleId });

        affected.Should().Be(1, "the role this test created is the only row addressed");
    }

    /// <summary>Counts the role rows whose optional subscription terms are all null.</summary>
    /// <param name="roleId">The role to examine.</param>
    /// <returns>One when every optional term of that role is null, otherwise zero.</returns>
    private Task<int> CountClearedTermsAsync(int roleId)
    {
        return _fixture.Database.ScalarAsync<int>(
            "SELECT COUNT(*) FROM [dbo].[Roles] WHERE [RoleID] = @roleId "
            + "AND [RoleGroupID] IS NULL AND [BillingPeriod] IS NULL AND [BillingFrequency] IS NULL "
            + "AND [TrialFee] IS NULL AND [TrialPeriod] IS NULL AND [TrialFrequency] IS NULL "
            + "AND [RSVPCode] IS NULL AND [IconFile] IS NULL",
            new Dictionary<string, object?> { ["roleId"] = roleId });
    }

    /// <summary>
    /// Counts the role rows whose three optional text columns hold empty strings rather than nulls.
    /// </summary>
    /// <param name="roleId">The role to examine.</param>
    /// <returns>One when all three columns are present and empty, otherwise zero.</returns>
    /// <remarks>
    /// The emptiness test is <c>LEN</c> against zero with an explicit not-null test alongside it, because
    /// <c>= ''</c> alone would also be satisfied by a column holding only padding, and a null column would
    /// make the comparison unknown rather than false.
    /// </remarks>
    private Task<int> CountEmptyTextAsync(int roleId)
    {
        return _fixture.Database.ScalarAsync<int>(
            "SELECT COUNT(*) FROM [dbo].[Roles] WHERE [RoleID] = @roleId "
            + "AND [Description] IS NOT NULL AND LEN([Description]) = 0 "
            + "AND [RSVPCode] IS NOT NULL AND LEN([RSVPCode]) = 0 "
            + "AND [IconFile] IS NOT NULL AND LEN([IconFile]) = 0",
            new Dictionary<string, object?> { ["roleId"] = roleId });
    }

    /// <summary>Counts the role rows whose two flag columns hold a stored false rather than a null.</summary>
    /// <param name="roleId">The role to examine.</param>
    /// <returns>One when both flags are present and false, otherwise zero.</returns>
    private Task<int> CountStoredFalseFlagsAsync(int roleId)
    {
        return _fixture.Database.ScalarAsync<int>(
            "SELECT COUNT(*) FROM [dbo].[Roles] WHERE [RoleID] = @roleId "
            + "AND [IsPublic] IS NOT NULL AND [IsPublic] = 0 "
            + "AND [AutoAssignment] IS NOT NULL AND [AutoAssignment] = 0",
            new Dictionary<string, object?> { ["roleId"] = roleId });
    }

    /// <summary>Counts the membership rows of a role whose trial flag is null.</summary>
    /// <param name="roleId">The role to examine.</param>
    /// <returns>The number of rows whose trial flag is unknown.</returns>
    private Task<int> CountNullTrialFlagsAsync(int roleId)
    {
        return _fixture.Database.ScalarAsync<int>(
            "SELECT COUNT(*) FROM [dbo].[UserRoles] "
            + "WHERE [RoleID] = @roleId AND [IsTrialUsed] IS NULL",
            new Dictionary<string, object?> { ["roleId"] = roleId });
    }

    /// <summary>Counts the membership rows bearing the key an identity seeded at one can never issue.</summary>
    /// <returns>The number of such rows, which must be none.</returns>
    private Task<int> CountAssignmentsWithZeroKeyAsync()
    {
        return _fixture.Database.ScalarAsync<int>(
            "SELECT COUNT(*) FROM [dbo].[UserRoles] WHERE [UserRoleID] = 0");
    }

    /// <summary>Reads the identity seed a table's identity column was declared with.</summary>
    /// <param name="qualifiedTableName">The schema-qualified table name.</param>
    /// <returns>The declared seed.</returns>
    /// <remarks>
    /// Cast in the statement because the catalogue function answers a numeric type that does not convert
    /// directly, and read as a bound parameter so the table name is never composed into the text.
    /// </remarks>
    private Task<int> ReadIdentitySeedAsync(string qualifiedTableName)
    {
        return _fixture.Database.ScalarAsync<int>(
            "SELECT CAST(IDENT_SEED(@qualifiedTableName) AS int)",
            new Dictionary<string, object?> { ["qualifiedTableName"] = qualifiedTableName });
    }

    /// <summary>Reads the administrator role key a tenant row points at.</summary>
    /// <param name="portalId">The tenant to read.</param>
    /// <returns>The administrator role key.</returns>
    private Task<int> ReadAdministratorRoleIdAsync(int portalId)
    {
        return _fixture.Database.ScalarAsync<int>(
            "SELECT [AdministratorRoleId] FROM [dbo].[Portals] WHERE [PortalID] = @portalId",
            new Dictionary<string, object?> { ["portalId"] = portalId });
    }

    /// <summary>
    /// The two currency columns round-trip a value larger than a two-decimal-place column could hold, which
    /// is what distinguishes the legacy <c>money</c> type from a narrower numeric type.
    /// </summary>
    /// <remarks>
    /// This test exists because a mapping that is correct in the entity configuration can still be
    /// contradicted by the schema the suite runs against, and nothing else here would notice.
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

            await writeRoles.AddAsync(role);
            await unitOfWork.SaveChangesAsync();
            roleId = role.RoleId;
        }

        try
        {
            using IServiceScope scope = _fixture.Services.CreateScope();
            IRoleRepository roles = scope.ServiceProvider.GetRequiredService<IRoleRepository>();

            Role? stored = await roles.GetByIdAsync(roleId, portalId);

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
