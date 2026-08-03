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

            // MIGRATION: uniqueness needs no dedicated member. IX_RoleName is UNIQUE over
            // (PortalID, RoleName), so GetRoleByName (membership DataProvider.vb:L94) can match at most
            // one row and the row it matches IS the answer - including which role holds the name, which
            // a boolean could not report.
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
    /// <remarks>
    /// This is the set every newly registered account joins, so a role appearing here by mistake would grant
    /// access to everyone who signs up.
    /// </remarks>
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

            // MIGRATION: the membership provider had no auto-assigned procedure - it exposed
            // GetPortalRoles(PortalId) alone (DataProvider.vb:L91) and the caller tested the column. The
            // flag is therefore asserted on the rows this read returns, which is where the legacy
            // behaviour actually lived.
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
            // (04.08.00.SqlDataProvider:L41). The owned subset is selected because the same procedure
            // also admits installation-wide roles, which the next assertion covers explicitly.
            page.Where(role => role.PortalId == portalId).Select(role => role.RoleId)
                .Should().Equal(new[] { first, second, third });

            // Nothing belonging to another tenant may appear. Expressed as "no row owned by a different
            // portal" rather than as a positive predicate, so the assertion still means something when
            // the installation happens to define no host role at all.
            page.Where(role => role.PortalId != portalId && role.PortalId != null)
                .Should().BeEmpty("a tenant listing may not disclose another tenant's roles");

            IReadOnlyList<Role> elsewhere = await roles.GetByPortalIdAsync(UnknownPortalId);
            elsewhere.Where(role => role.PortalId != null).Should().BeEmpty();
            elsewhere.Select(role => role.RoleId).Should().NotContain(first);

            // MIGRATION: THE READ ADMITS ROLES WITH NO OWNING PORTAL. The terminal GetPortalRoles filters
            // on ( R.PortalId = @PortalId OR R.PortalId is null ) at 04.08.00.SqlDataProvider:L40, so an
            // installation-wide role is visible to every tenant. That is asserted with a real host role
            // rather than inferred, because the seeded installation defines none and an absent case would
            // let a strict-equality regression pass unnoticed. Note the contrast with GetByIdAsync, whose
            // terminal procedure is a strict equality - the two are deliberately asymmetric.
            Role hostRole = new()
            {
                PortalId = null,
                RoleName = FormattableString.Invariant($"Host {marker}"),
                Description = "Created by the persistence role suite.",
            };

            using (IServiceScope hostScope = _fixture.Services.CreateScope())
            {
                IRoleRepository hostRoles = hostScope.ServiceProvider.GetRequiredService<IRoleRepository>();
                IUnitOfWork hostUnitOfWork = hostScope.ServiceProvider.GetRequiredService<IUnitOfWork>();

                await hostRoles.AddAsync(hostRole);
                await hostUnitOfWork.SaveChangesAsync();
            }

            try
            {
                using IServiceScope withHost = _fixture.Services.CreateScope();
                IRoleRepository reading = withHost.ServiceProvider.GetRequiredService<IRoleRepository>();

                (await reading.GetByPortalIdAsync(portalId)).Select(role => role.RoleId)
                    .Should().Contain(hostRole.RoleId, "a role with no owning portal is visible to every tenant");

                // The same role is unreachable through the by-key read, whose portal condition is strict.
                (await reading.GetByIdAsync(hostRole.RoleId, portalId)).Should()
                    .BeNull("GetRole compares PortalId for equality, which a null never satisfies");
            }
            finally
            {
                await RemoveRoleAsync(hostRole.RoleId);
            }
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
    /// <remarks>
    /// Realises membership <c>DataProvider.vb:L92 GetRoles()</c>, which took no argument and applied no
    /// filter. It is the one role read that is deliberately not tenant-scoped, so a caller answering a
    /// question about one portal must not reach for it.
    /// </remarks>
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
            // what "no filter" means and is exactly why this member is not the one a tenant-scoped
            // caller should use.
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
    /// <remarks>
    /// Realises membership <c>DataProvider.vb:L115 GetServices(PortalId, UserId)</c>. The terminal
    /// procedure at <c>04.05.00.SqlDataProvider:L36-L37</c> filters on
    /// <c>R.PortalId = @PortalId and R.IsPublic = 1</c>, so a private role must never appear here: it
    /// would offer a subscription to a grouping the tenant never published. The portal condition is a
    /// strict equality there, so installation-wide roles are excluded as well.
    /// </remarks>
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
    /// <remarks>
    /// The group name is projected alongside each role, so the navigation has to be loaded by the listing
    /// rather than left for a caller to fetch one row at a time.
    /// </remarks>
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

            // Realises membership DataProvider.vb:L105 GetRolesByGroup(RoleGroupId, PortalId).
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

                // MIGRATION: group-name uniqueness is settled over this list. The membership provider
                // exposed GetRoleGroups(portalId) (DataProvider.vb:L104) and no existence procedure, so
                // the comparison was always the caller's.
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

        // Realises membership DataProvider.vb:L109 GetUserRole(PortalID, UserId, RoleId), in that
        // argument order.
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

        // MIGRATION: dbo.UserRoles has no portal column, so the tenant anchor is taken from the role the
        // assignment points at. A real assignment asked about under another tenant is therefore absent,
        // which is what stops one tenant answering another tenant's membership question.
        (await roles.GetUserRoleAsync(UnknownPortalId, _fixture.Seed.AdminUserId, _fixture.Seed.AdministratorRoleId))
            .Should().BeNull();
    }

    /// <summary>Assignments resolve by login name, and the role name narrows the answer optionally.</summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// Realises membership <c>DataProvider.vb:L111 GetUserRolesByUsername(PortalID, Username,
    /// Rolename)</c>. The legacy role argument was optional and a null meant "every role", which is why
    /// it is the one nullable argument on the contract. An empty string is a different question: the
    /// legacy no-string marker WAS the empty string, so it narrows to a role of that name rather than
    /// widening to all of them, and the two must not be conflated.
    /// </remarks>
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
    /// The legacy absent-date marker is replaced by SQL <c>NULL</c> on the way to the store, on both
    /// bounds and on both write members.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// <para>
    /// MIGRATION: this is <c>Null.GetNull</c>, asserted against a real SQL Server rather than against a
    /// mock. The legacy write path converted the marker on every write - both members that wrote these
    /// columns wrapped both values, at membership <c>DataProvider/SqlDataProvider.vb</c> L280-L286 - and
    /// the repository members that stand in for them do the same, which is what keeps the stored shape
    /// identical under Rule T5 and keeps the sentinel out of the Domain under Rule T7.
    /// </para>
    /// <para>
    /// The test would fail LOUDLY without the normalisation rather than subtly: both columns are
    /// <c>datetime</c>, whose range begins at 1753-01-01, so the server refuses 0001-01-01 outright and
    /// the save would throw a range error. That is precisely why the legacy layer converted the value,
    /// and it is why this belongs in an integration test - an in-memory or mocked store would accept the
    /// marker happily and prove nothing.
    /// </para>
    /// <para>
    /// The marker carries a time component, because the legacy emptiness test compared DATE PARTS ONLY
    /// (<c>Null.vb</c> L183-L186, "this avoids subtle time differences") and a value copied out of a
    /// legacy object may well have one attached. An exact-equality test would let it through.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Assignment_NormalisesTheLegacyAbsentDateMarkerToNull()
    {
        int portalId = await CreatePortalAsync();
        int roleId = await CreateRoleAsync(portalId, FormattableString.Invariant($"Marker {Suffix()}"));
        DateTime marker = DateTime.MinValue.AddHours(5);

        try
        {
            using (IServiceScope adding = _fixture.Services.CreateScope())
            {
                IRoleRepository roles = adding.ServiceProvider.GetRequiredService<IRoleRepository>();
                IUnitOfWork unitOfWork = adding.ServiceProvider.GetRequiredService<IUnitOfWork>();

                await roles.AddUserRoleAsync(new UserRole
                {
                    UserId = _fixture.Seed.MemberUserId,
                    RoleId = roleId,
                    EffectiveDate = marker,
                    ExpiryDate = marker,
                });

                await unitOfWork.SaveChangesAsync();
            }

            (await CountNullBoundsAsync(roleId)).Should().Be(
                1,
                "an added assignment whose bounds carry the marker is stored with both columns null");

            using (IServiceScope amending = _fixture.Services.CreateScope())
            {
                IRoleRepository roles = amending.ServiceProvider.GetRequiredService<IRoleRepository>();
                IUnitOfWork unitOfWork = amending.ServiceProvider.GetRequiredService<IUnitOfWork>();

                UserRole? assignment = await roles.GetUserRoleAsync(portalId, _fixture.Seed.MemberUserId, roleId);
                assignment.Should().NotBeNull();

                // A real bound first, so the update genuinely has something to overwrite and the null
                // that follows cannot be mistaken for the value simply never having changed.
                assignment!.ExpiryDate = new DateTime(2027, 6, 1, 0, 0, 0, DateTimeKind.Utc);
                await roles.UpdateUserRoleAsync(assignment);
                await unitOfWork.SaveChangesAsync();
            }

            (await CountNullBoundsAsync(roleId)).Should().Be(0, "the expiry now holds a real instant");

            using (IServiceScope clearing = _fixture.Services.CreateScope())
            {
                IRoleRepository roles = clearing.ServiceProvider.GetRequiredService<IRoleRepository>();
                IUnitOfWork unitOfWork = clearing.ServiceProvider.GetRequiredService<IUnitOfWork>();

                UserRole? assignment = await roles.GetUserRoleAsync(portalId, _fixture.Seed.MemberUserId, roleId);
                assignment.Should().NotBeNull();

                assignment!.ExpiryDate = marker;
                await roles.UpdateUserRoleAsync(assignment);
                await unitOfWork.SaveChangesAsync();
            }

            (await CountNullBoundsAsync(roleId)).Should().Be(
                1,
                "and an amendment back to the marker clears the column, exactly as the legacy "
                + "UpdateUserRole did by wrapping the same value in GetNull");

            using IServiceScope reading = _fixture.Services.CreateScope();
            IRoleRepository reader = reading.ServiceProvider.GetRequiredService<IRoleRepository>();

            UserRole? stored = await reader.GetUserRoleAsync(portalId, _fixture.Seed.MemberUserId, roleId);
            stored.Should().NotBeNull();
            stored!.EffectiveDate.Should().BeNull("what comes back is absence, not the marker");
            stored.ExpiryDate.Should().BeNull();
            stored.GetStatus(DateTime.UtcNow).Should().Be(
                RoleStatus.Active,
                "so the Domain classifies it as in force without needing to recognise a sentinel");
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
    /// This is how a role that automatically assigns itself enrols the existing membership at the moment it is
    /// created. The identifier is not known until the save happens, and it may turn out to be zero, which is
    /// indistinguishable from an unset value. Referring to the role object instead leaves the store to fill in
    /// the identifier, so the pattern is correct whatever value the identity column produces.
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

                // Both stagings return a bare task: neither yields the generated key, which is exactly
                // what lets the role and its first member commit as one unit even though the key is not
                // known until they do.
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
    /// The cascade is declared in the schema rather than performed by the application. Leaving the membership
    /// rows behind would leave assignments naming a role that no longer exists, and the permission evaluation
    /// resolves role names by joining through exactly those rows.
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

    /// <summary>
    /// Counts the membership rows of a role whose two bounds are both SQL <c>NULL</c>.
    /// </summary>
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
