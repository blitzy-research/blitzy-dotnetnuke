using DnnMigration.Domain.Abstractions.Repositories;
using DnnMigration.Domain.Entities;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DnnMigration.IntegrationTests.Persistence;

/// <summary>
/// Verifies that permission-catalogue reads honour the resource identifiers supplied to the repository.
/// </summary>
[Trait("Category", "Integration")]
[Collection(IntegrationTestCollection.Name)]
public sealed class PermissionRepositoryTests
{
    private const int UnknownTabId = 987_654_321;

    /// <summary>
    /// The all-users pseudo-principal, defined by the legacy source as <c>glbRoleAllUsers = "-1"</c> at
    /// <c>Library/Components/Shared/Globals.vb</c>:L95 and stored in the grant tables' role column.
    /// </summary>
    private const int AllUsersPseudoRoleId = -1;

    /// <summary>
    /// The folder identifier the folder-grant rows in this suite name. Arbitrary and unreferenced: the
    /// legacy folder grant table declares no foreign key to the folder table, and no folder table exists in
    /// this suite's schema, so the value only has to be stable within a test.
    /// </summary>
    private const int LegacyFolderId = 1;

    private readonly ApiTestFixture _fixture;

    /// <summary>Initialises a new instance of the <see cref="PermissionRepositoryTests"/> class.</summary>
    /// <param name="fixture">The shared database and seed.</param>
    public PermissionRepositoryTests(ApiTestFixture fixture) => _fixture = fixture;

    /// <summary>An identifier naming no page returns no catalogue definitions.</summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task GetByTabIdAsync_WhenThePageDoesNotExist_ReturnsNoMetadata()
    {
        using IServiceScope scope = _fixture.Services.CreateScope();
        IPermissionRepository permissions =
            scope.ServiceProvider.GetRequiredService<IPermissionRepository>();

        IReadOnlyList<Permission> definitions = await permissions
            .GetByTabIdAsync(UnknownTabId, CancellationToken.None);

        definitions.Should().BeEmpty();
    }

    /// <summary>An existing page receives the shared page-scope catalogue.</summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task GetByTabIdAsync_WhenThePageExists_ReturnsThePageScope()
    {
        using IServiceScope scope = _fixture.Services.CreateScope();
        IPermissionRepository permissions =
            scope.ServiceProvider.GetRequiredService<IPermissionRepository>();

        IReadOnlyList<Permission> definitions = await permissions
            .GetByTabIdAsync(_fixture.Seed.RootTabId, CancellationToken.None);

        definitions.Should().NotBeEmpty();
        definitions.Should().OnlyContain(
            definition => definition.PermissionCode == IntegrationSeed.TabPermissionCode);
        definitions.Select(definition => definition.PermissionId).Should()
            .Contain(_fixture.Seed.TabViewPermissionId)
            .And.Contain(_fixture.Seed.TabEditPermissionId);
    }

    /// <summary>
    /// the role-scoped module and page removals take exactly the grants addressed to the named role and
    /// nothing else.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// The two removals reproduce the middle statements of the terminal <c>DeleteRole</c> procedure,
    /// <c>03.00.10.SqlDataProvider</c> - <c>delete from ModulePermission where RoleId = @RoleId</c> and the
    /// same over <c>TabPermission</c>.
    /// </remarks>
    [Fact]
    public async Task DeleteGrantsByRoleId_TakesOnlyTheGrantsAddressedToThatRole()
    {
        int doomedRoleId = await CreateRoleAsync("Sweep target " + Suffix());
        int retainedRoleId = await CreateRoleAsync("Sweep bystander " + Suffix());
        int moduleId = await CreateModuleAsync();
        int tabId = _fixture.Seed.RootTabId;
        int accountId = _fixture.Seed.MemberUserId;

        List<int> pageGrants = [];

        try
        {
            await SeedModuleGrantsAsync(moduleId, doomedRoleId, retainedRoleId, accountId);

            foreach ((int? roleId, int? userId) in PrincipalMatrix(doomedRoleId, retainedRoleId, accountId))
            {
                pageGrants.Add(await SeedPageGrantAsync(tabId, roleId, userId));
            }

            using IServiceScope scope = _fixture.Services.CreateScope();
            IPermissionRepository permissions =
                scope.ServiceProvider.GetRequiredService<IPermissionRepository>();

            await permissions.DeleteModulePermissionsByRoleIdAsync(doomedRoleId, CancellationToken.None);

            (await CountModuleGrantsForRoleAsync(moduleId, doomedRoleId)).Should().Be(0);
            (await CountModuleGrantsForRoleAsync(moduleId, retainedRoleId)).Should().Be(
                1,
                "a second role's grant is not this role's to remove");
            (await CountModuleGrantsForRoleAsync(moduleId, AllUsersPseudoRoleId)).Should().Be(
                1,
                "the pseudo-principal names no role row, so no role removal may discard it");
            (await CountModuleGrantsForAccountAsync(moduleId, accountId)).Should().Be(
                1,
                "the role column is nullable, and a grant addressed to an account must not be matched");

            await permissions.DeleteTabPermissionsByRoleIdAsync(doomedRoleId, CancellationToken.None);

            (await CountPageGrantsForRoleAsync(tabId, doomedRoleId)).Should().Be(0);
            (await CountPageGrantsForRoleAsync(tabId, retainedRoleId)).Should().Be(1);
            (await CountPageGrantsForRoleAsync(tabId, AllUsersPseudoRoleId)).Should().Be(1);
            (await CountPageGrantsByKeyAsync(pageGrants)).Should().Be(
                3,
                "exactly one of the four page grants seeded here was addressed to the removed role");
        }
        finally
        {
            await RemoveModuleAsync(moduleId);
            await RemovePageGrantsAsync(pageGrants);
            await RemoveRoleAsync(doomedRoleId);
            await RemoveRoleAsync(retainedRoleId);
        }
    }

    /// <summary>
    /// the folder removal takes the named role's rows out of the legacy folder grant table when that table
    /// is present.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// So the table is created HERE, for the duration of this one test, in the terminal shape the legacy
    /// chain arrives at: <c>02.02.00.SqlDataProvider</c>:L659 creates it with a NOT NULL role column and
    /// <c>04.05.00.SqlDataProvider</c>:L750-L790 rebuilds that column as nullable and adds an account
    /// column.
    /// </remarks>
    [Fact]
    public async Task DeleteFolderPermissionsByRoleId_WhenTheLegacyTableExists_TakesOnlyThatRolesRows()
    {
        int doomedRoleId = await CreateRoleAsync("Folder sweep target " + Suffix());
        int retainedRoleId = await CreateRoleAsync("Folder sweep bystander " + Suffix());

        try
        {
            await CreateLegacyFolderGrantTableAsync();

            await _fixture.Database.ExecuteAsync(
                """
                INSERT INTO [dbo].[FolderPermission]
                    ([FolderID], [PermissionID], [RoleID], [UserID], [AllowAccess])
                VALUES (@folderId, @permissionId, @doomedRoleId, NULL, 1),
                       (@folderId, @permissionId, @retainedRoleId, NULL, 1),
                       (@folderId, @permissionId, @allUsersRoleId, NULL, 1),
                       (@folderId, @permissionId, NULL, @accountId, 1);
                """,
                new Dictionary<string, object?>
                {
                    ["folderId"] = LegacyFolderId,
                    ["permissionId"] = _fixture.Seed.TabViewPermissionId,
                    ["doomedRoleId"] = doomedRoleId,
                    ["retainedRoleId"] = retainedRoleId,
                    ["allUsersRoleId"] = AllUsersPseudoRoleId,
                    ["accountId"] = _fixture.Seed.MemberUserId,
                });

            using IServiceScope scope = _fixture.Services.CreateScope();
            IPermissionRepository permissions =
                scope.ServiceProvider.GetRequiredService<IPermissionRepository>();

            await permissions.DeleteFolderPermissionsByRoleIdAsync(doomedRoleId, CancellationToken.None);

            (await CountFolderGrantsForRoleAsync(doomedRoleId)).Should().Be(0);
            (await CountFolderGrantsForRoleAsync(retainedRoleId)).Should().Be(
                1,
                "a second role's folder grant is not this role's to remove");
            (await CountFolderGrantsForRoleAsync(AllUsersPseudoRoleId)).Should().Be(
                1,
                "the pseudo-principal names no role row");
            (await CountFolderGrantsForAccountAsync(_fixture.Seed.MemberUserId)).Should().Be(
                1,
                "the role column is nullable here too, and an account-addressed grant must not be matched");
        }
        finally
        {
            await DropLegacyFolderGrantTableAsync();
            await RemoveRoleAsync(doomedRoleId);
            await RemoveRoleAsync(retainedRoleId);
        }
    }

    /// <summary>
    /// the folder removal is a no-op against a database that does not have the legacy folder grant table,
    /// and creates nothing in order to reach that outcome.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// The absence is asserted BEFORE and AFTER, so the test cannot pass by the member quietly provisioning
    /// what it needs. The precondition also documents the fact the sibling test above depends on: this
    /// suite's committed schema does not declare the table.
    /// </remarks>
    [Fact]
    public async Task DeleteFolderPermissionsByRoleId_WhenTheLegacyTableIsAbsent_RemovesNothingAndCreatesNothing()
    {
        (await LegacyFolderGrantTableExistsAsync()).Should().Be(
            0,
            "the schema this suite provisions declares no folder grant table, which is the state under test");

        using IServiceScope scope = _fixture.Services.CreateScope();
        IPermissionRepository permissions =
            scope.ServiceProvider.GetRequiredService<IPermissionRepository>();

        await permissions.DeleteFolderPermissionsByRoleIdAsync(
            _fixture.Seed.SubscribersRoleId,
            CancellationToken.None);

        (await LegacyFolderGrantTableExistsAsync()).Should().Be(
            0,
            "reaching the no-op outcome must not involve provisioning the table");
    }

    /// <summary>Creates a role in the seeded tenant through the repository.</summary>
    /// <param name="roleName">The role name, which must be unique within the tenant.</param>
    /// <returns>The identifier the store assigned.</returns>
    private async Task<int> CreateRoleAsync(string roleName)
    {
        using IServiceScope scope = _fixture.Services.CreateScope();
        IRoleRepository roles = scope.ServiceProvider.GetRequiredService<IRoleRepository>();
        IUnitOfWork unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        Role role = new()
        {
            PortalId = _fixture.Seed.PortalId,
            RoleName = roleName,
            Description = "Created by the permission persistence suite.",
        };

        await roles.AddAsync(role);
        await unitOfWork.SaveChangesAsync();

        return role.RoleId;
    }

    /// <summary>Removes a role created by this suite.</summary>
    /// <param name="roleId">The role to remove.</param>
    /// <returns>A task that completes once the role is gone.</returns>
    private async Task RemoveRoleAsync(int roleId)
    {
        using IServiceScope scope = _fixture.Services.CreateScope();
        IRoleRepository roles = scope.ServiceProvider.GetRequiredService<IRoleRepository>();
        IUnitOfWork unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        await roles.DeleteAsync(roleId);
        await unitOfWork.SaveChangesAsync();
    }

    /// <summary>Creates a module in the seeded tenant, to hang module grants from.</summary>
    /// <returns>The identifier the store assigned.</returns>
    private Task<int> CreateModuleAsync() => _fixture.Database.ScalarAsync<int>(
        """
        INSERT INTO [dbo].[Modules]
            ([ModuleDefID], [PortalID], [ModuleTitle], [AllTabs], [IsDeleted], [InheritViewPermissions])
        VALUES (@moduleDefinitionId, @portalId, N'Grant sweep module', 0, 0, 0);
        SELECT CAST(SCOPE_IDENTITY() AS int);
        """,
        new Dictionary<string, object?>
        {
            ["moduleDefinitionId"] = _fixture.Seed.ModuleDefinitionId,
            ["portalId"] = _fixture.Seed.PortalId,
        });

    /// <summary>Removes a module and, by cascade, every grant recorded against it.</summary>
    /// <param name="moduleId">The module to remove.</param>
    /// <returns>A task that completes once the module is gone.</returns>
    private async Task RemoveModuleAsync(int moduleId) => _ = await _fixture.Database.ExecuteAsync(
        "DELETE FROM [dbo].[Modules] WHERE [ModuleID] = @moduleId;",
        new Dictionary<string, object?> { ["moduleId"] = moduleId });

    /// <summary>
    /// The four principals every grant assertion in this file is made over: the role under removal, an
    /// unrelated role, the all-users pseudo-principal, and an account.
    /// </summary>
    /// <param name="doomedRoleId">The role whose grants are removed.</param>
    /// <param name="retainedRoleId">An unrelated role whose grants must survive.</param>
    /// <param name="accountId">An account whose own grant must survive.</param>
    /// <returns>The role and account column values, in that order, for each of the four grants.</returns>
    private static (int? RoleId, int? UserId)[] PrincipalMatrix(
        int doomedRoleId,
        int retainedRoleId,
        int accountId) =>
        [
            (doomedRoleId, null),
            (retainedRoleId, null),
            (AllUsersPseudoRoleId, null),
            (null, accountId),
        ];

    /// <summary>Records the four-principal module grant matrix against one module.</summary>
    /// <param name="moduleId">The module the grants are recorded against.</param>
    /// <param name="doomedRoleId">The role whose grants are removed.</param>
    /// <param name="retainedRoleId">An unrelated role whose grant must survive.</param>
    /// <param name="accountId">An account whose own grant must survive.</param>
    /// <returns>A task that completes once the rows exist.</returns>
    private async Task SeedModuleGrantsAsync(
        int moduleId,
        int doomedRoleId,
        int retainedRoleId,
        int accountId)
    {
        foreach ((int? roleId, int? userId) in PrincipalMatrix(doomedRoleId, retainedRoleId, accountId))
        {
            _ = await _fixture.Database.ExecuteAsync(
                """
                INSERT INTO [dbo].[ModulePermission]
                    ([ModuleID], [PermissionID], [RoleID], [UserID], [AllowAccess])
                VALUES (@moduleId, @permissionId, @roleId, @userId, 1);
                """,
                new Dictionary<string, object?>
                {
                    ["moduleId"] = moduleId,
                    ["permissionId"] = _fixture.Seed.ModuleViewPermissionId,
                    ["roleId"] = roleId,
                    ["userId"] = userId,
                });
        }
    }

    /// <summary>Records one page grant and returns its key.</summary>
    /// <param name="tabId">The page the grant is recorded against.</param>
    /// <param name="roleId">The role the grant addresses, or <see langword="null"/>.</param>
    /// <param name="userId">The account the grant addresses, or <see langword="null"/>.</param>
    /// <returns>The identifier the store assigned.</returns>
    private Task<int> SeedPageGrantAsync(int tabId, int? roleId, int? userId) =>
        _fixture.Database.ScalarAsync<int>(
            """
            INSERT INTO [dbo].[TabPermission]
                ([TabID], [PermissionID], [RoleID], [UserID], [AllowAccess])
            VALUES (@tabId, @permissionId, @roleId, @userId, 1);
            SELECT CAST(SCOPE_IDENTITY() AS int);
            """,
            new Dictionary<string, object?>
            {
                ["tabId"] = tabId,
                ["permissionId"] = _fixture.Seed.TabViewPermissionId,
                ["roleId"] = roleId,
                ["userId"] = userId,
            });

    /// <summary>Removes page grants this suite recorded, named by key.</summary>
    /// <param name="tabPermissionIds">The keys to remove.</param>
    /// <returns>A task that completes once the rows are gone.</returns>
    private async Task RemovePageGrantsAsync(IReadOnlyList<int> tabPermissionIds)
    {
        foreach (int tabPermissionId in tabPermissionIds)
        {
            _ = await _fixture.Database.ExecuteAsync(
                "DELETE FROM [dbo].[TabPermission] WHERE [TabPermissionID] = @tabPermissionId;",
                new Dictionary<string, object?> { ["tabPermissionId"] = tabPermissionId });
        }
    }

    /// <summary>Counts the module grants addressed to one role on one module.</summary>
    /// <param name="moduleId">The module whose grants are counted.</param>
    /// <param name="roleId">The role the grants address.</param>
    /// <returns>The number of matching rows.</returns>
    private Task<int> CountModuleGrantsForRoleAsync(int moduleId, int roleId) =>
        _fixture.Database.ScalarAsync<int>(
            "SELECT COUNT(*) FROM [dbo].[ModulePermission] "
            + "WHERE [ModuleID] = @moduleId AND [RoleID] = @roleId;",
            new Dictionary<string, object?> { ["moduleId"] = moduleId, ["roleId"] = roleId });

    /// <summary>Counts the module grants addressed to one account on one module.</summary>
    /// <param name="moduleId">The module whose grants are counted.</param>
    /// <param name="userId">The account the grants address.</param>
    /// <returns>The number of matching rows.</returns>
    private Task<int> CountModuleGrantsForAccountAsync(int moduleId, int userId) =>
        _fixture.Database.ScalarAsync<int>(
            "SELECT COUNT(*) FROM [dbo].[ModulePermission] "
            + "WHERE [ModuleID] = @moduleId AND [UserID] = @userId;",
            new Dictionary<string, object?> { ["moduleId"] = moduleId, ["userId"] = userId });

    /// <summary>Counts the page grants addressed to one role on one page.</summary>
    /// <param name="tabId">The page whose grants are counted.</param>
    /// <param name="roleId">The role the grants address.</param>
    /// <returns>The number of matching rows.</returns>
    private Task<int> CountPageGrantsForRoleAsync(int tabId, int roleId) =>
        _fixture.Database.ScalarAsync<int>(
            "SELECT COUNT(*) FROM [dbo].[TabPermission] "
            + "WHERE [TabID] = @tabId AND [RoleID] = @roleId;",
            new Dictionary<string, object?> { ["tabId"] = tabId, ["roleId"] = roleId });

    /// <summary>Counts how many of exactly four page grants survive, named by key.</summary>
    /// <param name="tabPermissionIds">The four keys, in insertion order.</param>
    /// <returns>The number of those rows that survive.</returns>
    private Task<int> CountPageGrantsByKeyAsync(IReadOnlyList<int> tabPermissionIds) =>
        _fixture.Database.ScalarAsync<int>(
            "SELECT COUNT(*) FROM [dbo].[TabPermission] "
            + "WHERE [TabPermissionID] IN (@first, @second, @third, @fourth);",
            new Dictionary<string, object?>
            {
                ["first"] = tabPermissionIds[0],
                ["second"] = tabPermissionIds[1],
                ["third"] = tabPermissionIds[2],
                ["fourth"] = tabPermissionIds[3],
            });

    /// <summary>Counts the folder grants addressed to one role.</summary>
    /// <param name="roleId">The role the grants address.</param>
    /// <returns>The number of matching rows.</returns>
    private Task<int> CountFolderGrantsForRoleAsync(int roleId) =>
        _fixture.Database.ScalarAsync<int>(
            "SELECT COUNT(*) FROM [dbo].[FolderPermission] WHERE [RoleID] = @roleId;",
            new Dictionary<string, object?> { ["roleId"] = roleId });

    /// <summary>Counts the folder grants addressed to one account.</summary>
    /// <param name="userId">The account the grants address.</param>
    /// <returns>The number of matching rows.</returns>
    private Task<int> CountFolderGrantsForAccountAsync(int userId) =>
        _fixture.Database.ScalarAsync<int>(
            "SELECT COUNT(*) FROM [dbo].[FolderPermission] WHERE [UserID] = @userId;",
            new Dictionary<string, object?> { ["userId"] = userId });

    /// <summary>Reports whether the legacy folder grant table is present.</summary>
    /// <returns>One when the table exists, zero when it does not.</returns>
    private Task<int> LegacyFolderGrantTableExistsAsync() => _fixture.Database.ScalarAsync<int>(
        "SELECT CASE WHEN OBJECT_ID(N'[dbo].[FolderPermission]', N'U') IS NULL THEN 0 ELSE 1 END;");

    /// <summary>Creates the legacy folder grant table in its terminal shape, for the duration of one test.</summary>
    /// <returns>A task that completes once the table exists.</returns>
    /// <remarks>
    /// The shape is taken from the legacy chain rather than invented: <c>02.02.00.SqlDataProvider</c>:L659
    /// declares the identity key, the folder and permission columns and the access flag, and
    /// <c>04.05.00.SqlDataProvider</c>:L750-L790 rebuilds the role column as nullable and adds the account
    /// column.
    /// </remarks>
    private async Task CreateLegacyFolderGrantTableAsync() => _ = await _fixture.Database.ExecuteAsync(
        """
        IF OBJECT_ID(N'[dbo].[FolderPermission]', N'U') IS NULL
            CREATE TABLE [dbo].[FolderPermission] (
                [FolderPermissionID] int NOT NULL IDENTITY,
                [FolderID] int NOT NULL,
                [PermissionID] int NOT NULL,
                [RoleID] int NULL,
                [UserID] int NULL,
                [AllowAccess] bit NOT NULL,
                CONSTRAINT [PK_FolderPermission] PRIMARY KEY ([FolderPermissionID])
            );
        """);

    /// <summary>Drops the legacy folder grant table this suite created.</summary>
    /// <returns>A task that completes once the table is gone.</returns>
    private async Task DropLegacyFolderGrantTableAsync() => _ = await _fixture.Database.ExecuteAsync(
        "DROP TABLE IF EXISTS [dbo].[FolderPermission];");

    /// <summary>Builds a short random suffix, so a role name cannot collide across runs.</summary>
    /// <returns>Twelve hexadecimal characters.</returns>
    private static string Suffix() => Guid.NewGuid().ToString("N")[..12];
}
