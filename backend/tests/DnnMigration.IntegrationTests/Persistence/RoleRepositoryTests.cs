using DnnMigration.Domain.Abstractions.Repositories;
using DnnMigration.Domain.Common;
using DnnMigration.Domain.Entities;
using DnnMigration.Domain.Enums;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
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

            // MIGRATION: THE READ CARRIES A PREDICATE THAT THE TERMINAL COLUMN LEAVES UNSATISFIABLE, and
            // this suite used to fabricate a row to exercise it. The terminal GetPortalRoles filters on
            // ( R.PortalId = @PortalId OR R.PortalId is null ) at 04.08.00.SqlDataProvider:L40, and the
            // repository reproduces that predicate faithfully - but Roles.PortalID is int NOT NULL in the
            // terminal schema (01.00.05.SqlDataProvider:2749, corroborated by the fresh-install snapshot at
            // DotNetNuke.Schema.SqlDataProvider:6209), so no installation can hold such a row and the null
            // branch can never be true.
            //
            // The earlier version of this test inserted a role with PortalId = null and asserted that the
            // listing returned it. It passed only because Schema/DnnSchema.sql had drifted to declaring the
            // column NULL, which measuring against Schema/TerminalSchema.manifest exposed. Asserting
            // behaviour over data production cannot hold is worse than asserting nothing, because it reads
            // downstream as proof of a capability that does not exist. The constraint that makes the branch
            // unreachable is proved instead by LegacySchemaFidelityTests
            // Roles_RefusesARoleThatBelongsToNoPortal, and the rollback on that refusal by the sibling test
            // below. The predicate itself stays in the repository because the legacy procedure wrote it and
            // the Minimal Change Clause protects that, not because anything can exercise it.
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
    /// The two assignment write members stage both validity bounds exactly as they are handed them: a null
    /// stays a null, a real instant is stored verbatim, and the legacy absent-date marker is refused by the
    /// store rather than quietly reinterpreted.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// <para>
    /// WHAT THIS ORACLE CHANGED AND WHY. An earlier revision asserted that the REPOSITORY reproduced
    /// <c>Null.GetNull</c> - that it read a submitted <c>DateTime.MinValue</c> as absence before staging.
    /// That translation is a subscription rule and <c>RoleService.NormalizeLegacyDateMarker</c> already
    /// owned it, so the persistence layer held a second implementation of one rule: two copies that agree
    /// until one is amended, and then disagree on exactly the case that prompted the amendment. Rule T2
    /// gives interpretation to the Application layer, so the repository copy was removed and this test now
    /// pins the contract that replaced it - a write member decides nothing.
    /// </para>
    /// <para>
    /// THE THIRD ASSERTION IS THE LOAD-BEARING ONE. Both columns are <c>datetime</c>, whose range begins at
    /// 1753-01-01, so 0001-01-01 is not merely absent from them but UNSTORABLE. Proving that the store
    /// refuses it is what makes the Application-layer translation demonstrably necessary rather than
    /// merely tidy, and it proves that nothing between the entity and the column silently hides the marker
    /// - which is precisely the guarantee <c>UserRoleConfiguration</c> claims by installing no value
    /// conversion. It also has to be an INTEGRATION assertion: an in-memory store accepts 0001-01-01
    /// happily and would prove nothing at all.
    /// </para>
    /// <para>
    /// The marker carries a time component, because the legacy emptiness test compared DATE PARTS ONLY
    /// (<c>Null.vb</c> L183-L186, "this avoids subtle time differences") and a value copied out of a
    /// legacy object may well have one attached. The Application-layer rule matches that comparison; here
    /// the value serves only to prove the column rejects it.
    /// </para>
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
                // having changed. It is stated in the PAST deliberately: the repository must persist it
                // unchanged rather than advancing it to the present instant, because that is what keeps the
                // expire-rather-than-delete cancellation - which back-dates an expiry by one day - reachable
                // at all.
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

    /// <summary>
    /// Every paid-membership term is amended through the update member and read back from the store.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// <para>
    /// The terms are asserted together rather than one per test because they are one commercial
    /// statement: a fee without its period and frequency does not describe a subscription, and a mapping
    /// that carried the fee but dropped the period would leave a role priced but never billed. The group
    /// is reassigned in the same amendment, so the update path is proven to move a role between groups
    /// and not merely to rewrite its own columns.
    /// </para>
    /// <para>
    /// MIGRATION: THE LEGACY UPDATE TOOK THIRTEEN POSITIONAL ARGUMENTS AND DISAGREED WITH ITS OWN STORE.
    /// Membership <c>DataProvider.vb</c> declared
    /// <c>UpdateRole(RoleId, RoleGroupId, Description, ServiceFee As Single, BillingPeriod As String,
    /// BillingFrequency As String, TrialFee As Single, TrialPeriod As Integer, TrialFrequency As String,
    /// IsPublic, AutoAssignment, RSVPCode, IconFile)</c> - a floating-point fee and a STRING billing
    /// period - while <c>RoleInfo.vb</c> exposed <c>BillingPeriod</c> as an <c>Integer</c> and the
    /// terminal schema declares it <c>int NULL</c>. Under Rule T4 the store settles the disagreement, so
    /// the amendment below works in <c>decimal?</c> fees and <c>int?</c> periods and never in a
    /// <c>Single</c> or a numeric string. The positional list itself is gone: the entity carries the
    /// terms and one member stages it.
    /// </para>
    /// <para>
    /// The fee is deliberately 999.99, the largest value a <c>decimal(5, 2)</c> column could hold. It is
    /// asserted here because that narrower type is what an early reading of the baseline DDL suggests -
    /// <c>01.00.00.SqlDataProvider</c> declares <c>[ServiceFee] [decimal](5, 2) NULL</c> - whereas the
    /// TERMINAL schema this suite runs against declares <c>money</c>, which the sibling assertion on a
    /// five-figure fee proves. Both facts matter: the ceiling value must round-trip exactly, and no
    /// assertion here may claim a ceiling the running store does not in fact impose.
    /// </para>
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
    /// <para>
    /// This is the half of the update path that a round-trip test alone never reaches. Writing a value and
    /// reading it back proves the column is bound; writing an absence over a value proves the binding
    /// carries absence too, which is what a role reverting from paid to free requires. Every term
    /// involved is optional in the terminal schema - <c>BillingPeriod</c> and <c>TrialPeriod</c> are
    /// <c>int NULL</c>, the two frequencies are <c>char(1) NULL</c>, and <c>RSVPCode</c> and
    /// <c>IconFile</c> are nullable text - so the entity models each of them as a nullable CLR type and
    /// nothing here has to recognise a sentinel to express "no value" (Rule T7).
    /// </para>
    /// <para>
    /// The group membership is cleared in the same amendment. MIGRATION: the legacy row expressed "in no
    /// group" as the <c>Null.NullInteger</c> marker -1 rather than as SQL <c>NULL</c>, and -1 is
    /// simultaneously a legitimate <c>Portals.PortalID</c>, so the marker was never safe to read as
    /// absence. The target models the relationship as <c>int?</c> and the absence is a genuine null.
    /// </para>
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
    /// <remarks>
    /// The group members are read back afterwards to prove the amendment did not disturb them. The legacy
    /// <c>UpdateRoleGroup(RoleGroupId, GroupName, Description)</c> carried exactly these two mutable
    /// values and nothing else, so a target that also touched the group's roles would be doing more than
    /// the member it stands in for.
    /// </remarks>
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
    /// <remarks>
    /// <para>
    /// MIGRATION: ALL SIX CODES ARE LOAD-BEARING, NOT FOUR. The legacy
    /// <c>Library/Components/Security/Roles/RoleController.vb</c> branches on the literal characters
    /// <c>N</c>, <c>O</c>, <c>D</c>, <c>W</c>, <c>M</c> and <c>Y</c> in one <c>Select Case</c>, and the two
    /// non-interval codes carry as much meaning as the four intervals: <c>N</c> assigned the legacy
    /// absent-date marker, meaning the membership never lapses, and <c>O</c> assigned the literal
    /// <c>New System.DateTime(9999, 12, 31)</c>, meaning a single fee buys perpetual access. A migration
    /// that carried only the four interval codes would silently reprice every free and every one-off role
    /// in an existing installation.
    /// </para>
    /// <para>
    /// The stored character is asserted directly rather than inferred from the round-trip, because a
    /// conversion that persisted the enumeration's ORDINAL would round-trip perfectly through this same
    /// code path while writing bytes no existing DotNetNuke database contains and no legacy procedure can
    /// read. The character is the contract; the member is an alias for it. That is also why the members
    /// carry the code points as their values, which the <c>ushort</c> base of the enumeration permits.
    /// </para>
    /// <para>
    /// Both frequency columns are exercised with the same code in one row, because they are two
    /// independent columns sharing one conversion and a mapping that bound only the billing column would
    /// otherwise pass.
    /// </para>
    /// </remarks>
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
    /// <para>
    /// THIS IS THE MOST CONSEQUENTIAL ASSERTION IN THIS FILE, and the reason is in the shipped data rather
    /// than in the code. <c>Roles.BillingFrequency</c> is <c>char(1) NULL</c>, which accepts any single
    /// character, and the installation seed in <c>01.00.00.SqlDataProvider</c> takes that literally: the
    /// Administrators role is inserted with <c>BillingFrequency = '4'</c> and the Registered Users role
    /// with <c>'0'</c>. Neither is in <c>{N, O, D, W, M, Y}</c>. A strict <c>Enum.Parse</c>, or a
    /// conversion that raised on an unrecognised code, would therefore be unable to read the two roles
    /// present in every single installation - including the Administrators role, whose key is the target
    /// of <c>Portals.AdministratorRoleId</c> and whose membership the tenant-administration policy grants
    /// on. Role administration would fail at the read, before any business rule ran.
    /// </para>
    /// <para>
    /// MIGRATION: THE LEGACY SWITCH HAD NO <c>Case Else</c>, AND THE TARGET REPRODUCES ITS TOLERANCE
    /// WITHOUT REPRODUCING ITS SILENCE. An unrecognised code fell through every arm of the legacy
    /// <c>Select Case</c> and left the expiry date exactly as it was - it did not raise, did not clear the
    /// value and did not substitute a default. The target conversion is tolerant in the same direction and
    /// CARRIES the unrecognised character rather than resolving it to a declared member: the enumeration is
    /// backed by <c>ushort</c> and each member IS its own code point, so any stored character is
    /// representable exactly and the write direction casts the identical character back.
    /// </para>
    /// <para>
    /// MIGRATION: an earlier revision normalised an unrecognised code to the member whose meaning is "not
    /// billed", on the reasoning that a frequency nobody can understand should generate no billing event.
    /// That reading was defensible in isolation and wrong in context, for two measured reasons. A role
    /// update rewrites both frequency columns from the materialised value, so editing an unrelated field
    /// silently rewrote a stored <c>'4'</c> as <c>'N'</c> - destroying data AAP Rule T4 makes authoritative.
    /// And it was not even behaviour-preserving: the legacy trial test was
    /// <c>TrialFrequency.ToString() &lt;&gt; "N"</c> (<c>RoleController.vb</c> L521), which an unrecognised
    /// character SATISFIES, so normalising to None flipped whether the trial governed the derived expiry.
    /// The preservation is asserted below, in the read-modify-write shape that the normalisation could not
    /// have survived.
    /// </para>
    /// <para>
    /// Both codes are read through the ordinary repository members, single and listing, so the tolerance is
    /// proven on the paths the application actually uses rather than on a bespoke query. The unrecognised
    /// character is placed in the column by an <c>UPDATE</c> because no code path in the target can write
    /// one - which is the point: the target never produces such a row, and must still read one.
    /// </para>
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

            // MIGRATION: the resolution is deliberately NOT null. A null frequency means "this role
            // carries no frequency at all", which is what an untouched legacy column means; the seeded
            // rows do carry a character, and flattening the two cases together would lose the fact that
            // something unreadable was stored. The distinction is asserted here so that a later change to
            // the conversion cannot quietly merge them.
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
    /// <para>
    /// THIS IS THE ASSERTION THAT DISTINGUISHES A TOLERANT READ FROM A LOSSLESS ONE, and it is the one that
    /// matters, because a tolerant-but-lossy read passes every test that only reads. A role update writes
    /// both frequency columns from whatever the read materialised, so a read that resolved an unrecognised
    /// character to a declared member turned every unrelated edit into a silent, irreversible rewrite of
    /// authoritative legacy data. AAP Rule T4 makes the existing database authoritative; nothing in this
    /// migration may edit a column the caller did not ask to change.
    /// </para>
    /// <para>
    /// The two columns are planted with DIFFERENT characters on purpose. A single shared character would
    /// let a conversion that read one column and wrote both pass, and the two columns are the same store
    /// type over the same vocabulary bound by the same shared converter instance - so proving they move
    /// independently is what proves neither is being written from the other.
    /// </para>
    /// <para>
    /// The characters are the ones every installation ships: <c>'4'</c> on the Administrators role and
    /// <c>'0'</c> on the Registered Users role (<c>01.00.00.SqlDataProvider</c> L7192 and L7194). Only a
    /// direct statement can plant them, because no code path in the target produces such a character - and
    /// that is the point: the target never writes one and must never destroy one either.
    /// </para>
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
    /// conversion. A DETACHED role is written in full, so every column goes to the store including both
    /// frequencies - which means only a genuinely lossless conversion can leave them intact. Reading in one
    /// scope and writing in another is what makes the instance detached.
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
    /// <para>
    /// MIGRATION: EVERY LEGACY INSERT RETURNED THE GENERATED KEY; NONE OF THESE DOES. Each legacy
    /// procedure ended in <c>SCOPE_IDENTITY()</c> and its provider member was declared
    /// <c>As Integer</c> - <c>AddRole</c>, <c>AddRoleGroup</c> and <c>AddUserRole</c> alike - so the
    /// caller received the key at the moment of the call and the call therefore had to be its own
    /// transaction. The target inverts that: <c>AddAsync</c> returns a bare <c>Task</c>, stages an
    /// intention, and the key appears on the entity only once <c>IUnitOfWork.SaveChangesAsync</c> has run.
    /// That is what allows one commit to span the several tables a tenant creation writes, and it is why
    /// no member of this contract can return an <c>int</c>. The absence of a return value is proven at
    /// compile time by the staging calls in this file being statements rather than assignments.
    /// </para>
    /// <para>
    /// The proof of "not yet persisted" is the SEPARATE-SCOPE READ, not the numeric value of the key.
    /// <c>Roles.RoleID</c> is <c>IDENTITY(0, 1)</c>, so zero is a legitimate role identifier and an
    /// unwritten entity is indistinguishable from the first role of an installation by inspecting the
    /// property alone. The uncommitted value is asserted as well, but only as the CLR default it is, with
    /// the visibility check carrying the actual claim.
    /// </para>
    /// <para>
    /// <c>Entity{TId}.IdentityIsPersisted</c> is deliberately NOT asserted to become true after the
    /// commit. Nothing in this solution declares it automatically - there is no interceptor and no
    /// post-save hook calling <c>MarkIdentityPersisted</c> - so it remains false on an entity the store
    /// has just written, and a test asserting otherwise would be asserting a mechanism that does not
    /// exist.
    /// </para>
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
    /// <para>
    /// This is the commit boundary the whole staging arrangement exists for, exercised across all three
    /// tables this contract owns. The legacy engine could not express it: each of
    /// <c>AddRoleGroup</c>, <c>AddRole</c> and <c>AddUserRole</c> was a separate procedure returning its
    /// own <c>SCOPE_IDENTITY()</c>, so the caller had to commit the group before it could name it in the
    /// role, and commit the role before it could name it in the assignment. A failure between two of
    /// those calls left the tenant holding a group with no roles, or a role with no members, with no
    /// mechanism to undo it.
    /// </para>
    /// <para>
    /// The two dependent rows refer to their parents through NAVIGATIONS rather than through keys, which
    /// is the only formulation that works before the keys exist. It is also the formulation that survives
    /// the identity seeds of this schema unharmed: the group's key may legitimately be zero and so may
    /// the role's, so any code that waited for a non-zero key before wiring up a child would wait for
    /// ever on the first group and the first role of an installation.
    /// </para>
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
    /// <para>
    /// The classification itself is a pure Domain method taking the instant as a parameter, so it needs no
    /// database to exercise. What needs one is the claim that the classification is STABLE ACROSS
    /// PERSISTENCE: the two bounds are SQL Server <c>datetime</c> columns, whose resolution is coarser
    /// than a CLR <c>DateTime</c>, and a membership that reported one status in memory and another after a
    /// round-trip would be an authorisation answer that changed when nothing about the grant did. Both
    /// bounds are therefore whole days, which <c>datetime</c> represents exactly, and both readings are
    /// asserted against the same fixed instant.
    /// </para>
    /// <para>
    /// The instant is far in the future so that the write-path normalisation does not consume the cases.
    /// An effective bound already in the past is deliberately cleared to null on the way to the store -
    /// the legacy <c>If EffectiveDate &lt; Now Then EffectiveDate = Null.NullDate</c> - so a case built
    /// around a past start would arrive as an unbounded one and would prove the wrong thing.
    /// </para>
    /// <para>
    /// MIGRATION: BOTH BOUNDS ARE INCLUSIVE, AND EXPIRY OUTRANKS A START THAT HAS NOT ARRIVED. The
    /// inclusive reading is the measured behaviour of the terminal <c>GetRolesByUser</c> predicate, which
    /// admits a membership sitting exactly on either bound, so the zero-offset case below is in force
    /// rather than lapsed. The precedence matters because the two bounds really can contradict each other:
    /// the cancellation path back-dates the expiry by a day and never touches the effective date, so a
    /// cancelled future membership exists in ordinary data. Reporting it as pending would tell an
    /// administrator it was about to begin.
    /// </para>
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
    /// <para>
    /// <c>Roles.RoleID</c> is <c>IDENTITY(0, 1)</c> and <c>UserRoles.UserRoleID</c> is
    /// <c>IDENTITY(1, 1)</c>, so within this one contract zero is a legitimate role key and is not a
    /// legitimate assignment key. Neither fact is safe to generalise from the other, and code that
    /// standardised on either reading would be wrong half the time here: rejecting zero as invalid makes
    /// the Administrators role of every installation unaddressable, while accepting it as an assignment
    /// key admits a value the store never issues.
    /// </para>
    /// <para>
    /// The seeds are read from the catalogue as well as observed through the repository, because a suite
    /// running against a database whose seeds had been normalised would otherwise pass while proving
    /// nothing about the schema the application must actually work with.
    /// </para>
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
    /// <para>
    /// MIGRATION: THE LEGACY WRITE PATH TURNED BOTH OF THESE INTO <c>NULL</c>, AND THE TARGET DOES NOT.
    /// <c>Null.vb</c> defines <c>NullString</c> as the EMPTY STRING and <c>NullBoolean</c> as
    /// <see langword="false"/>, and <c>Null.GetNull</c> substituted <c>DBNull</c> for any value equal to
    /// the marker for its type. An empty description and a false flag were therefore both persisted as
    /// <c>NULL</c> by the legacy layer, which made "the administrator cleared this field" and "this field
    /// was never set" the same stored row, and made a false flag indistinguishable from an unknown one.
    /// </para>
    /// <para>
    /// The target writes what it is given. That is a deliberate divergence in the stored bytes rather than
    /// an oversight, and it is the right way round: the two boolean columns are <c>NOT NULL</c> in the
    /// terminal schema, so <c>false</c> has a home in them and nothing is lost, while
    /// <c>UserRoles.IsTrialUsed</c> is genuinely nullable and is modelled <c>bool?</c> precisely so that a
    /// legacy <c>NULL</c> remains representable and is not silently read as "no trial used". Both halves
    /// are asserted, because the divergence is only safe if the nullable column really does keep its null.
    /// </para>
    /// <para>
    /// Evidence that the legacy store is full of such empty strings rather than nulls, so that reading one
    /// back as a null would be a live behavioural change: the seeded tenant row holds <c>HostFee</c> as
    /// an empty string, and the seeded module rows hold their authorised-role lists the same way.
    /// </para>
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

            // The nullable flag on the assignment keeps its null, which is the other half of the trade:
            // the divergence above is only acceptable because absence remains expressible where the
            // schema genuinely permits it.
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
    /// <para>
    /// <strong>This test replaces one that asserted the opposite, and the correction is the point.</strong>
    /// It used to insert a role with <c>PortalId = null</c> and assert that every tenant's listing returned
    /// it, on the strength of <c>Roles.PortalID</c> being <c>int NULL</c>. That declaration was wrong:
    /// <c>Schema/DnnSchema.sql</c> had drifted, and measuring it against the independently derived
    /// <c>Schema/TerminalSchema.manifest</c> showed the terminal column is <c>int NOT NULL</c>
    /// (<c>01.00.05.SqlDataProvider:2749</c>, corroborated by the fresh-install snapshot at
    /// <c>DotNetNuke.Schema.SqlDataProvider:6209</c>). The old test therefore proved a capability no
    /// installation has, and it could only ever have passed against a fixture that was itself wrong.
    /// </para>
    /// <para>
    /// <strong>What is asserted here instead is a repository property rather than a schema one.</strong>
    /// That the column refuses the row is proved by <c>LegacySchemaFidelityTests</c>
    /// <c>Roles_RefusesARoleThatBelongsToNoPortal</c>; what this adds is that the refusal is CLEAN - the
    /// unit of work leaves nothing behind, and a subsequent read through a fresh scope sees precisely the
    /// roles that existed before the attempt. A half-applied write would be invisible to the schema
    /// assertion and visible only here.
    /// </para>
    /// <para>
    /// MIGRATION: the repository still reproduces the terminal listing predicate
    /// <c>( R.PortalId = @PortalId OR R.PortalId is null )</c>
    /// (<c>04.08.00.SqlDataProvider:L40</c>) and the strict equality of the single read
    /// (<c>04.00.04.SqlDataProvider:L311-L336</c>). Against a faithful installation the null branch of the
    /// first is unsatisfiable, so the asymmetry between the two is unobservable. It is preserved because the
    /// legacy procedures wrote it and the Minimal Change Clause protects them, and it is recorded as
    /// unobservable rather than left looking like tested behaviour.
    /// </para>
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

    /// <summary>
    /// Counts the role rows whose optional subscription terms are all null.
    /// </summary>
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

    /// <summary>
    /// Counts the role rows whose two flag columns hold a stored false rather than a null.
    /// </summary>
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
