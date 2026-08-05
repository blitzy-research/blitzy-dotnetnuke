using DnnMigration.Domain.Abstractions.Repositories;
using DnnMigration.Domain.Entities;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DnnMigration.IntegrationTests.Persistence;

/// <summary>
/// Proves that staging an UPDATE for a detached entity whose primary key is a low identity seed writes to
/// the existing row instead of inserting a duplicate.
/// </summary>
/// <remarks>
/// <para>
/// WHY THIS SUITE EXISTS, AND WHY IT COVERS FIVE TABLES RATHER THAN ONE. Entity Framework Core's
/// <c>DbSet&lt;T&gt;.Update</c> and <c>DbSet&lt;T&gt;.Attach</c> do not ask the caller what state the entity
/// is in; they infer it, and they infer <c>Added</c> whenever a store-generated integer key holds the CLR
/// default. That inference is safe only for a table whose identity begins at 1, because it silently equates
/// "the key is zero" with "the key was never assigned". Five tables in this schema break that equivalence:
/// </para>
/// <list type="bullet">
///   <item><description><c>dbo.Portals.PortalID</c> is <c>IDENTITY(-1, 1)</c>, so an installation's first
///   two tenants are numbered -1 and 0 - and -1 is simultaneously the legacy absent-integer sentinel.</description></item>
///   <item><description><c>dbo.Tabs.TabID</c> is <c>IDENTITY(0, 1)</c>, so a tenant's first page is page 0.</description></item>
///   <item><description><c>dbo.Roles.RoleID</c> is <c>IDENTITY(0, 1)</c>, so a tenant's first role - by
///   convention its administrators role, the most privileged there is - is role 0.</description></item>
///   <item><description><c>dbo.RoleGroups.RoleGroupID</c> is <c>IDENTITY(0, 1)</c>.</description></item>
///   <item><description><c>dbo.Modules.ModuleID</c> is <c>IDENTITY(0, 1)</c>.</description></item>
/// </list>
/// <para>
/// The failure mode is silent and destructive rather than loud: the caller receives a success, the row it
/// meant to change is untouched, a duplicate appears, and any value the application layer derives from the
/// row it believes it just saved - a page's path, for instance - is then computed against the wrong row. A
/// unit test cannot catch it, because the inference only decides anything once a real store-generated key is
/// in play, which is why these assertions live in the integration suite and read the row back from the
/// store.
/// </para>
/// <para>
/// Every case here deliberately obtains its entity in ONE scope and stages the change in ANOTHER. That is
/// not ceremony: an entity the writing context already tracks takes a different branch entirely, and it is
/// the DETACHED branch that carries the defect. Reading through a second scope reproduces exactly what a
/// request does when it reads through a no-tracking query, hands the entity across a layer boundary, and
/// stages it back.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
[Collection(IntegrationTestCollection.Name)]
public sealed class IdentitySeedUpdateTests
{
    private readonly ApiTestFixture _fixture;

    /// <summary>Initialises a new instance of the <see cref="IdentitySeedUpdateTests"/> class.</summary>
    /// <param name="fixture">The shared host and database.</param>
    public IdentitySeedUpdateTests(ApiTestFixture fixture) => _fixture = fixture;

    /// <summary>
    /// Staging a detached page whose identifier is the zero identity seed updates that page and adds no row.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// The page's descendants are checked as well. A staged insert took a new identifier, so the path the
    /// application layer then recomputed for the children was composed against a page that had only just
    /// appeared - which is how a sibling's stored path came to name a page its parent had never been renamed
    /// to.
    /// </remarks>
    [Fact]
    public async Task TabUpdate_AtTheZeroIdentitySeed_WritesTheExistingRow()
    {
        _fixture.Seed.RootTabId.Should().Be(
            0,
            "dbo.Tabs.TabID is IDENTITY(0, 1), so the first page of a freshly created database is page zero");

        int before = await CountAsync("[dbo].[Tabs]");
        string original = await TabNameAsync(_fixture.Seed.RootTabId);
        string renamed = FormattableString.Invariant($"Home {Guid.NewGuid():N}");

        try
        {
            Tab detached = await ReadTabAsync(_fixture.Seed.RootTabId);
            detached.TabName = renamed;
            detached.Title = renamed;

            using (IServiceScope writing = _fixture.Services.CreateScope())
            {
                ITabRepository tabs = writing.ServiceProvider.GetRequiredService<ITabRepository>();
                IUnitOfWork unitOfWork = writing.ServiceProvider.GetRequiredService<IUnitOfWork>();

                await tabs.UpdateAsync(detached);
                await unitOfWork.SaveChangesAsync();
            }

            (await CountAsync("[dbo].[Tabs]")).Should().Be(
                before,
                "an update must not insert; a new row here means the key was read as unset");
            (await TabNameAsync(_fixture.Seed.RootTabId)).Should().Be(
                renamed,
                "the edit must land on page zero rather than on a duplicate");
        }
        finally
        {
            await RestoreTabNameAsync(_fixture.Seed.RootTabId, original);
        }
    }

    /// <summary>
    /// Staging a detached page's position when its identifier is the zero identity seed updates that page,
    /// adds no row, and still writes only the four ordering columns.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// The narrowness is asserted alongside the row count because the two properties are in tension: the
    /// member has to begin tracking the entity without consulting its key AND without widening the write to
    /// the columns the caller did not mean to change. A name carried on the same instance is therefore
    /// deliberately altered and expected NOT to reach the store.
    /// </remarks>
    [Fact]
    public async Task TabOrderUpdate_AtTheZeroIdentitySeed_WritesTheExistingRowAndOnlyTheOrderingColumns()
    {
        int before = await CountAsync("[dbo].[Tabs]");
        string original = await TabNameAsync(_fixture.Seed.RootTabId);
        int originalOrder = await TabOrderAsync(_fixture.Seed.RootTabId);

        try
        {
            Tab detached = await ReadTabAsync(_fixture.Seed.RootTabId);
            detached.TabOrder = originalOrder + 20;
            detached.TabName = FormattableString.Invariant($"Never Written {Guid.NewGuid():N}");

            using (IServiceScope writing = _fixture.Services.CreateScope())
            {
                ITabRepository tabs = writing.ServiceProvider.GetRequiredService<ITabRepository>();
                IUnitOfWork unitOfWork = writing.ServiceProvider.GetRequiredService<IUnitOfWork>();

                await tabs.UpdateOrderAsync(detached);
                await unitOfWork.SaveChangesAsync();
            }

            (await CountAsync("[dbo].[Tabs]")).Should().Be(before);
            (await TabOrderAsync(_fixture.Seed.RootTabId)).Should().Be(originalOrder + 20);
            (await TabNameAsync(_fixture.Seed.RootTabId)).Should().Be(
                original,
                "this member writes four ordering columns and must carry no unrelated edit along with them");
        }
        finally
        {
            await _fixture.Database.ExecuteAsync(
                "UPDATE [dbo].[Tabs] SET [TabOrder] = @order WHERE [TabID] = @tabId",
                new Dictionary<string, object?> { ["order"] = originalOrder, ["tabId"] = _fixture.Seed.RootTabId })
                ;
        }
    }

    /// <summary>
    /// Staging a detached role whose identifier is the zero identity seed updates that role and adds no row.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task RoleUpdate_AtTheZeroIdentitySeed_WritesTheExistingRow()
    {
        _fixture.Seed.AdministratorRoleId.Should().Be(
            0,
            "dbo.Roles.RoleID is IDENTITY(0, 1), so the administrators role of a fresh database is role zero");

        int before = await CountAsync("[dbo].[Roles]");
        string described = FormattableString.Invariant($"Described {Guid.NewGuid():N}");

        Role detached;
        using (IServiceScope reading = _fixture.Services.CreateScope())
        {
            IRoleRepository roles = reading.ServiceProvider.GetRequiredService<IRoleRepository>();
            detached = (await roles.GetByIdAsync(_fixture.Seed.AdministratorRoleId, _fixture.Seed.PortalId)
                )!;
        }

        detached.Should().NotBeNull();
        string original = detached.Description ?? string.Empty;

        try
        {
            detached.Description = described;

            using IServiceScope writing = _fixture.Services.CreateScope();
            IRoleRepository roles = writing.ServiceProvider.GetRequiredService<IRoleRepository>();
            IUnitOfWork unitOfWork = writing.ServiceProvider.GetRequiredService<IUnitOfWork>();

            await roles.UpdateAsync(detached);
            await unitOfWork.SaveChangesAsync();

            (await CountAsync("[dbo].[Roles]")).Should().Be(before);
            (await ScalarStringAsync(
                "SELECT [Description] FROM [dbo].[Roles] WHERE [RoleID] = @key",
                _fixture.Seed.AdministratorRoleId))
                .Should().Be(described);
        }
        finally
        {
            await _fixture.Database.ExecuteAsync(
                "UPDATE [dbo].[Roles] SET [Description] = @value WHERE [RoleID] = @key",
                new Dictionary<string, object?> { ["value"] = original, ["key"] = _fixture.Seed.AdministratorRoleId })
                ;
        }
    }

    /// <summary>
    /// Staging a detached role group whose identifier is the zero identity seed updates that group and adds
    /// no row.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// The group is placed at identifier zero explicitly rather than by relying on it being the first group
    /// the database ever had. Suites in this collection share one database and may create groups of their
    /// own, so the hazardous key is asserted into existence and removed afterwards; that keeps the case
    /// deterministic instead of dependent on execution order.
    /// </remarks>
    [Fact]
    public async Task RoleGroupUpdate_AtTheZeroIdentitySeed_WritesTheExistingRow()
    {
        const int hazardousKey = 0;
        bool inserted = await EnsureRoleGroupAtKeyAsync(hazardousKey);
        int before = await CountAsync("[dbo].[RoleGroups]");
        string renamed = FormattableString.Invariant($"Group {Guid.NewGuid():N}");

        try
        {
            RoleGroup detached;
            using (IServiceScope reading = _fixture.Services.CreateScope())
            {
                IRoleRepository roles = reading.ServiceProvider.GetRequiredService<IRoleRepository>();
                IReadOnlyList<RoleGroup> groups = await roles.GetRoleGroupsAsync(_fixture.Seed.PortalId)
                    ;
                detached = groups.Single(candidate => candidate.RoleGroupId == hazardousKey);
            }

            detached.RoleGroupName = renamed;

            using (IServiceScope writing = _fixture.Services.CreateScope())
            {
                IRoleRepository roles = writing.ServiceProvider.GetRequiredService<IRoleRepository>();
                IUnitOfWork unitOfWork = writing.ServiceProvider.GetRequiredService<IUnitOfWork>();

                await roles.UpdateRoleGroupAsync(detached);
                await unitOfWork.SaveChangesAsync();
            }

            (await CountAsync("[dbo].[RoleGroups]")).Should().Be(before);
            (await ScalarStringAsync(
                "SELECT [RoleGroupName] FROM [dbo].[RoleGroups] WHERE [RoleGroupID] = @key",
                hazardousKey))
                .Should().Be(renamed);
        }
        finally
        {
            if (inserted)
            {
                await _fixture.Database.ExecuteAsync(
                    "DELETE FROM [dbo].[RoleGroups] WHERE [RoleGroupID] = @key",
                    new Dictionary<string, object?> { ["key"] = hazardousKey });
            }
        }
    }

    /// <summary>
    /// Staging a detached module whose identifier is the zero identity seed updates that module and adds no
    /// row.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// The module repository already assigned the state rather than inferring it, so this case is a guard
    /// against regression rather than a fix being proven. It is included because the hazard is a property of
    /// the schema and not of any one repository, and a future edit that "simplified" this member back to
    /// <c>DbSet.Update</c> would otherwise pass every existing test.
    /// </remarks>
    [Fact]
    public async Task ModuleUpdate_AtTheZeroIdentitySeed_WritesTheExistingRow()
    {
        const int hazardousKey = 0;
        bool inserted = await EnsureModuleAtKeyAsync(hazardousKey);
        int before = await CountAsync("[dbo].[Modules]");
        string retitled = FormattableString.Invariant($"Module {Guid.NewGuid():N}");

        try
        {
            Module detached;
            using (IServiceScope reading = _fixture.Services.CreateScope())
            {
                IModuleRepository modules = reading.ServiceProvider.GetRequiredService<IModuleRepository>();
                detached = (await modules.GetByIdAsync(hazardousKey))!;
            }

            detached.Should().NotBeNull();
            detached.ModuleTitle = retitled;

            using (IServiceScope writing = _fixture.Services.CreateScope())
            {
                IModuleRepository modules = writing.ServiceProvider.GetRequiredService<IModuleRepository>();
                IUnitOfWork unitOfWork = writing.ServiceProvider.GetRequiredService<IUnitOfWork>();

                await modules.UpdateAsync(detached);
                await unitOfWork.SaveChangesAsync();
            }

            (await CountAsync("[dbo].[Modules]")).Should().Be(before);
            (await ScalarStringAsync(
                "SELECT [ModuleTitle] FROM [dbo].[Modules] WHERE [ModuleID] = @key",
                hazardousKey))
                .Should().Be(retitled);
        }
        finally
        {
            if (inserted)
            {
                await _fixture.Database.ExecuteAsync(
                    "DELETE FROM [dbo].[Modules] WHERE [ModuleID] = @key",
                    new Dictionary<string, object?> { ["key"] = hazardousKey });
            }
        }
    }

    /// <summary>
    /// Staging a detached tenant whose identifier is the negative identity seed updates that tenant and adds
    /// no row.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// Tenant -1 exercises the sentinel half of the collision and tenant 0 the unset-key half, so both are
    /// asserted. -1 is not the CLR default and would survive the inference; 0 would not, and 0 is an
    /// ordinary tenant here because the identity seed is negative.
    /// </remarks>
    [Fact]
    public async Task PortalUpdate_AtTheIdentitySeedAndAtZero_WritesTheExistingRows()
    {
        _fixture.Seed.PortalId.Should().Be(
            -1,
            "dbo.Portals.PortalID is IDENTITY(-1, 1), so the first tenant of a fresh database is tenant -1");

        int before = await CountAsync("[dbo].[Portals]");
        const int hazardousKey = 0;
        bool inserted = await EnsurePortalAtKeyAsync(hazardousKey);

        try
        {
            foreach (int portalId in new[] { _fixture.Seed.PortalId, hazardousKey })
            {
                string footer = FormattableString.Invariant($"Footer {Guid.NewGuid():N}");

                Portal detached;
                using (IServiceScope reading = _fixture.Services.CreateScope())
                {
                    IPortalRepository portals = reading.ServiceProvider.GetRequiredService<IPortalRepository>();
                    detached = (await portals.GetByIdAsync(portalId))!;
                }

                detached.Should().NotBeNull(FormattableString.Invariant($"tenant {portalId} must be readable"));
                detached.FooterText = footer;

                using (IServiceScope writing = _fixture.Services.CreateScope())
                {
                    IPortalRepository portals = writing.ServiceProvider.GetRequiredService<IPortalRepository>();
                    IUnitOfWork unitOfWork = writing.ServiceProvider.GetRequiredService<IUnitOfWork>();

                    await portals.UpdateAsync(detached);
                    await unitOfWork.SaveChangesAsync();
                }

                (await ScalarStringAsync(
                    "SELECT [FooterText] FROM [dbo].[Portals] WHERE [PortalID] = @key",
                    portalId))
                    .Should().Be(footer, FormattableString.Invariant($"tenant {portalId} must be updated in place"));
            }

            (await CountAsync("[dbo].[Portals]")).Should().Be(
                before + (inserted ? 1 : 0),
                "neither update may insert a tenant");
        }
        finally
        {
            if (inserted)
            {
                await _fixture.Database.ExecuteAsync(
                    "DELETE FROM [dbo].[Portals] WHERE [PortalID] = @key",
                    new Dictionary<string, object?> { ["key"] = hazardousKey });
            }
        }
    }

    /// <summary>Reads one page through its own scope so the returned instance is detached from any writer.</summary>
    /// <param name="tabId">The page to read.</param>
    /// <returns>The page.</returns>
    private async Task<Tab> ReadTabAsync(int tabId)
    {
        using IServiceScope reading = _fixture.Services.CreateScope();
        ITabRepository tabs = reading.ServiceProvider.GetRequiredService<ITabRepository>();

        Tab? tab = await tabs.GetByIdAsync(tabId);

        tab.Should().NotBeNull(FormattableString.Invariant($"page {tabId} must exist"));

        return tab!;
    }

    /// <summary>Counts the rows of a table.</summary>
    /// <param name="table">The bracketed, schema-qualified table name.</param>
    /// <returns>The row count.</returns>
    private Task<int> CountAsync(string table) =>
        _fixture.Database.ScalarAsync<int>(FormattableString.Invariant($"SELECT COUNT(*) FROM {table}"));

    /// <summary>Reads one page's stored name.</summary>
    /// <param name="tabId">The page.</param>
    /// <returns>The stored name.</returns>
    private Task<string> TabNameAsync(int tabId) =>
        ScalarStringAsync("SELECT [TabName] FROM [dbo].[Tabs] WHERE [TabID] = @key", tabId);

    /// <summary>Reads one page's stored position.</summary>
    /// <param name="tabId">The page.</param>
    /// <returns>The stored position.</returns>
    private Task<int> TabOrderAsync(int tabId) =>
        _fixture.Database.ScalarAsync<int>(
            "SELECT [TabOrder] FROM [dbo].[Tabs] WHERE [TabID] = @key",
            new Dictionary<string, object?> { ["key"] = tabId });

    /// <summary>Restores one page's name and title on the cleanup path.</summary>
    /// <param name="tabId">The page.</param>
    /// <param name="tabName">The name to restore.</param>
    /// <returns>A task that completes when the row is restored.</returns>
    private Task RestoreTabNameAsync(int tabId, string tabName) => _fixture.Database.ExecuteAsync(
        "UPDATE [dbo].[Tabs] SET [TabName] = @name, [Title] = @name WHERE [TabID] = @key",
        new Dictionary<string, object?> { ["name"] = tabName, ["key"] = tabId });

    /// <summary>Reads one string column addressed by a single integer key.</summary>
    /// <param name="sql">The statement, parameterised on <c>@key</c>.</param>
    /// <param name="key">The key value.</param>
    /// <returns>The stored text.</returns>
    private Task<string> ScalarStringAsync(string sql, int key) =>
        _fixture.Database.ScalarAsync<string>(sql, new Dictionary<string, object?> { ["key"] = key });

    /// <summary>Ensures a role group bears the supplied identifier.</summary>
    /// <param name="key">The identifier the group must bear.</param>
    /// <returns><see langword="true"/> when this call inserted the row and must remove it afterwards.</returns>
    private async Task<bool> EnsureRoleGroupAtKeyAsync(int key)
    {
        int affected = await _fixture.Database.ExecuteAsync(
            """
            IF NOT EXISTS (SELECT 1 FROM [dbo].[RoleGroups] WHERE [RoleGroupID] = @key)
            BEGIN
                SET IDENTITY_INSERT [dbo].[RoleGroups] ON;
                INSERT INTO [dbo].[RoleGroups] ([RoleGroupID], [PortalID], [RoleGroupName], [Description])
                VALUES (@key, @portalId, @name, N'Identity-seed regression fixture');
                SET IDENTITY_INSERT [dbo].[RoleGroups] OFF;
            END
            """,
            new Dictionary<string, object?>
            {
                ["key"] = key,
                ["portalId"] = _fixture.Seed.PortalId,
                ["name"] = FormattableString.Invariant($"Seed Guard {Guid.NewGuid():N}"),
            });

        return affected > 0;
    }

    /// <summary>Ensures a module bears the supplied identifier.</summary>
    /// <param name="key">The identifier the module must bear.</param>
    /// <returns><see langword="true"/> when this call inserted the row and must remove it afterwards.</returns>
    private async Task<bool> EnsureModuleAtKeyAsync(int key)
    {
        int affected = await _fixture.Database.ExecuteAsync(
            """
            IF NOT EXISTS (SELECT 1 FROM [dbo].[Modules] WHERE [ModuleID] = @key)
            BEGIN
                SET IDENTITY_INSERT [dbo].[Modules] ON;
                INSERT INTO [dbo].[Modules]
                    ([ModuleID], [ModuleDefID], [PortalID], [ModuleTitle], [AllTabs], [IsDeleted],
                     [InheritViewPermissions])
                VALUES (@key, @moduleDefinitionId, @portalId, N'Identity-seed regression fixture', 0, 0, 0);
                SET IDENTITY_INSERT [dbo].[Modules] OFF;
            END
            """,
            new Dictionary<string, object?>
            {
                ["key"] = key,
                ["moduleDefinitionId"] = _fixture.Seed.ModuleDefinitionId,
                ["portalId"] = _fixture.Seed.PortalId,
            });

        return affected > 0;
    }

    /// <summary>Ensures a tenant bears the supplied identifier.</summary>
    /// <param name="key">The identifier the tenant must bear.</param>
    /// <returns><see langword="true"/> when this call inserted the row and must remove it afterwards.</returns>
    private async Task<bool> EnsurePortalAtKeyAsync(int key)
    {
        int affected = await _fixture.Database.ExecuteAsync(
            """
            IF NOT EXISTS (SELECT 1 FROM [dbo].[Portals] WHERE [PortalID] = @key)
            BEGIN
                SET IDENTITY_INSERT [dbo].[Portals] ON;
                INSERT INTO [dbo].[Portals]
                    ([PortalID], [PortalName], [FooterText], [UserRegistration], [BannerAdvertising],
                     [Currency], [HostFee], [HostSpace], [Description], [KeyWords], [GUID],
                     [DefaultLanguage], [TimezoneOffset], [HomeDirectory], [PageQuota], [UserQuota])
                VALUES (@key, @name, N'Seed guard footer', 2, 0, 'USD', N'0', 0,
                        N'Identity-seed regression fixture', N'integration', NEWID(), N'en-US', -8, '', 0, 0);
                SET IDENTITY_INSERT [dbo].[Portals] OFF;
            END
            """,
            new Dictionary<string, object?>
            {
                ["key"] = key,
                ["name"] = FormattableString.Invariant($"Seed Guard {Guid.NewGuid():N}"),
            });

        return affected > 0;
    }
}
