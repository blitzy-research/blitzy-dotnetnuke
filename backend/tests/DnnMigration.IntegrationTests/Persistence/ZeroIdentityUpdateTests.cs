using DnnMigration.Application.Abstractions;
using DnnMigration.Application.Dtos.Tab;
using DnnMigration.Domain.Abstractions.Repositories;
using DnnMigration.Domain.Common;
using DnnMigration.Domain.Entities;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DnnMigration.IntegrationTests.Persistence;

/// <summary>
/// Proves that staging an update for an entity whose real key is zero rewrites that row rather than
/// inserting a duplicate.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why this is a suite of its own.</strong> Four of this schema's identity columns are seeded below
/// one - <c>Portals.PortalID</c> at <c>IDENTITY(-1, 1)</c> (<c>01.00.00.SqlDataProvider:L77</c>) and
/// <c>Roles.RoleID</c>, <c>Tabs.TabID</c>, <c>Modules.ModuleID</c> at <c>IDENTITY(0, 1)</c>, with
/// <c>RoleGroups.RoleGroupID</c> joining them at <c>03.02.03.SqlDataProvider:L18</c>.
/// </para>
/// <para>
/// <strong>The defect this pins.</strong> <c>DbSet.Update</c> and <c>DbSet.Attach</c> decide between
/// <c>Added</c> and <c>Modified</c>/<c>Unchanged</c> by asking whether the key "is set", and they read an
/// <see cref="int"/> key of 0 as unset.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
[Collection(IntegrationTestCollection.Name)]
public sealed class ZeroIdentityUpdateTests
{
    /// <summary>The key every case in this suite addresses.</summary>
    private const int ZeroKey = 0;

    private readonly ApiTestFixture _fixture;

    /// <summary>Initialises a new instance of the <see cref="ZeroIdentityUpdateTests"/> class.</summary>
    /// <param name="fixture">The shared host and provisioned database.</param>
    public ZeroIdentityUpdateTests(ApiTestFixture fixture) => _fixture = fixture;

    /// <summary>
    /// A detached tenant whose key is zero is rewritten in place, and the tenant table does not grow.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// The row is planted with an explicit key because the provisioned database holds only the seeded
    /// tenant at -1, so zero is free; the legacy installation script plants its own tenant the same way,
    /// switching <c>IDENTITY_INSERT</c> on at <c>01.00.00.SqlDataProvider:L7123</c> before supplying
    /// <c>PortalID = 0</c> explicitly.
    /// </remarks>
    [Fact]
    public async Task PortalUpdate_OnADetachedKeyZeroTenant_RewritesTheRowAndDoesNotDuplicateIt()
    {
        string original = FormattableString.Invariant($"Zero Tenant {Suffix()}");
        string renamed = FormattableString.Invariant($"Zero Tenant Renamed {Suffix()}");

        await PlantKeyZeroPortalAsync(original);

        try
        {
            int before = await CountAsync("Portals");

            Portal detached;
            using (IServiceScope reading = _fixture.Services.CreateScope())
            {
                IPortalRepository portals = reading.ServiceProvider.GetRequiredService<IPortalRepository>();
                Portal? read = await portals.GetByIdAsync(ZeroKey);

                read.Should().NotBeNull("zero is a real tenant key and must be addressable");
                detached = read!;
            }

            detached.PortalName = renamed;

            using (IServiceScope writing = _fixture.Services.CreateScope())
            {
                IPortalRepository portals = writing.ServiceProvider.GetRequiredService<IPortalRepository>();
                IUnitOfWork unitOfWork = writing.ServiceProvider.GetRequiredService<IUnitOfWork>();

                await portals.UpdateAsync(detached);
                (await unitOfWork.SaveChangesAsync()).Should().Be(
                    1,
                    "one row is rewritten, so exactly one row is affected");
            }

            (await CountAsync("Portals")).Should().Be(
                before,
                "an update must not add a tenant; the defective path inserted a duplicate under a new key");

            (await NameOfPortalAsync(ZeroKey)).Should().Be(
                renamed,
                "the addressed row itself carries the change, rather than a duplicate carrying it");
        }
        finally
        {
            await _fixture.Database.ExecuteAsync(
                "DELETE FROM [dbo].[Portals] WHERE [PortalName] IN (@original, @renamed)",
                new Dictionary<string, object?> { ["original"] = original, ["renamed"] = renamed });
        }
    }

    /// <summary>A detached role whose key is zero is rewritten in place, and the role table does not grow.</summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// The seeded administrators role IS role zero, so it is the subject rather than a planted stand-in -
    /// which is the whole point, because that row is the target of <c>Portals.AdministratorRoleId</c> and
    /// the grant the tenant-administration policy tests. Its description is restored afterwards.
    /// </remarks>
    [Fact]
    public async Task RoleUpdate_OnADetachedKeyZeroRole_RewritesTheRowAndDoesNotDuplicateIt()
    {
        _fixture.Seed.AdministratorRoleId.Should().Be(
            ZeroKey,
            "the administrators role of a freshly provisioned installation carries the zero identity seed");

        string probe = FormattableString.Invariant($"Zero role probe {Suffix()}");
        string? originalDescription = null;

        try
        {
            int before = await CountAsync("Roles");

            Role detached;
            using (IServiceScope reading = _fixture.Services.CreateScope())
            {
                IRoleRepository roles = reading.ServiceProvider.GetRequiredService<IRoleRepository>();
                Role? read = await roles.GetByIdAsync(ZeroKey, _fixture.Seed.PortalId);

                read.Should().NotBeNull("zero is a real role key and must be addressable");
                detached = read!;
            }

            originalDescription = detached.Description;
            detached.Description = probe;

            using (IServiceScope writing = _fixture.Services.CreateScope())
            {
                IRoleRepository roles = writing.ServiceProvider.GetRequiredService<IRoleRepository>();
                IUnitOfWork unitOfWork = writing.ServiceProvider.GetRequiredService<IUnitOfWork>();

                await roles.UpdateAsync(detached);
                (await unitOfWork.SaveChangesAsync()).Should().Be(1);
            }

            (await CountAsync("Roles")).Should().Be(
                before,
                "an update must not add a role; the defective path duplicated the administrators role");

            using IServiceScope verifying = _fixture.Services.CreateScope();
            IRoleRepository verifier = verifying.ServiceProvider.GetRequiredService<IRoleRepository>();

            Role? reread = await verifier.GetByIdAsync(ZeroKey, _fixture.Seed.PortalId);
            reread.Should().NotBeNull();
            reread!.Description.Should().Be(probe);
            reread.RoleName.Should().Be(
                IntegrationSeed.AdministratorsRoleName,
                "the row rewritten is the seeded one rather than a new row wearing its values");
        }
        finally
        {
            await _fixture.Database.ExecuteAsync(
                "UPDATE [dbo].[Roles] SET [Description] = @description WHERE [RoleID] = @roleId",
                new Dictionary<string, object?>
                {
                    ["description"] = originalDescription,
                    ["roleId"] = ZeroKey,
                });
        }
    }

    /// <summary>
    /// A detached role group whose key is zero is rewritten in place, and the group table does not grow.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task RoleGroupUpdate_OnADetachedKeyZeroGroup_RewritesTheRowAndDoesNotDuplicateIt()
    {
        string original = FormattableString.Invariant($"Zero Group {Suffix()}");
        string renamed = FormattableString.Invariant($"Zero Group Renamed {Suffix()}");

        await PlantKeyZeroRoleGroupAsync(original);

        try
        {
            int before = await CountAsync("RoleGroups");

            RoleGroup detached;
            using (IServiceScope reading = _fixture.Services.CreateScope())
            {
                IRoleRepository roles = reading.ServiceProvider.GetRequiredService<IRoleRepository>();
                RoleGroup? read = await roles.GetRoleGroupAsync(_fixture.Seed.PortalId, ZeroKey);

                read.Should().NotBeNull("zero is a real group key and must be addressable");
                detached = read!;
            }

            detached.RoleGroupName = renamed;

            using (IServiceScope writing = _fixture.Services.CreateScope())
            {
                IRoleRepository roles = writing.ServiceProvider.GetRequiredService<IRoleRepository>();
                IUnitOfWork unitOfWork = writing.ServiceProvider.GetRequiredService<IUnitOfWork>();

                await roles.UpdateRoleGroupAsync(detached);
                (await unitOfWork.SaveChangesAsync()).Should().Be(1);
            }

            (await CountAsync("RoleGroups")).Should().Be(before, "an update must not add a group");

            using IServiceScope verifying = _fixture.Services.CreateScope();
            IRoleRepository verifier = verifying.ServiceProvider.GetRequiredService<IRoleRepository>();

            RoleGroup? reread = await verifier.GetRoleGroupAsync(_fixture.Seed.PortalId, ZeroKey);
            reread.Should().NotBeNull();
            reread!.RoleGroupName.Should().Be(renamed);
        }
        finally
        {
            await _fixture.Database.ExecuteAsync(
                "DELETE FROM [dbo].[RoleGroups] WHERE [RoleGroupName] IN (@original, @renamed)",
                new Dictionary<string, object?> { ["original"] = original, ["renamed"] = renamed });
        }
    }

    /// <summary>A detached page whose key is zero is rewritten in place, and the page table does not grow.</summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// The seeded home page IS page zero. A duplicate would join the portal's page tree, so every
    /// navigation, ordering and permission read that walks the tree would carry it - which is why the count
    /// matters more here than anywhere else.
    /// </remarks>
    [Fact]
    public async Task TabUpdate_OnADetachedKeyZeroPage_RewritesTheRowAndDoesNotDuplicateIt()
    {
        _fixture.Seed.RootTabId.Should().Be(
            ZeroKey,
            "the first page of a freshly provisioned installation carries the zero identity seed");

        string probe = FormattableString.Invariant($"Zero page probe {Suffix()}");
        string? originalTitle = null;

        try
        {
            int before = await CountAsync("Tabs");

            Tab detached;
            using (IServiceScope reading = _fixture.Services.CreateScope())
            {
                ITabRepository tabs = reading.ServiceProvider.GetRequiredService<ITabRepository>();
                Tab? read = await tabs.GetByIdAsync(ZeroKey);

                read.Should().NotBeNull("zero is a real page key and must be addressable");
                detached = read!;
            }

            originalTitle = detached.Title;
            detached.Title = probe;

            using (IServiceScope writing = _fixture.Services.CreateScope())
            {
                ITabRepository tabs = writing.ServiceProvider.GetRequiredService<ITabRepository>();
                IUnitOfWork unitOfWork = writing.ServiceProvider.GetRequiredService<IUnitOfWork>();

                await tabs.UpdateAsync(detached);
                (await unitOfWork.SaveChangesAsync()).Should().Be(1);
            }

            (await CountAsync("Tabs")).Should().Be(
                before,
                "an update must not add a page; the defective path grew the portal's page tree");

            using IServiceScope verifying = _fixture.Services.CreateScope();
            ITabRepository verifier = verifying.ServiceProvider.GetRequiredService<ITabRepository>();

            Tab? reread = await verifier.GetByIdAsync(ZeroKey);
            reread.Should().NotBeNull();
            reread!.Title.Should().Be(probe);
            reread.TabName.Should().Be(
                IntegrationSeed.RootTabName,
                "the row rewritten is the seeded home page rather than a new page wearing its values");
        }
        finally
        {
            await _fixture.Database.ExecuteAsync(
                "UPDATE [dbo].[Tabs] SET [Title] = @title WHERE [TabID] = @tabId",
                new Dictionary<string, object?> { ["title"] = originalTitle, ["tabId"] = ZeroKey });
        }
    }

    /// <summary>
    /// The positional page write renumbers a detached page whose key is zero, and writes only its four
    /// positional columns.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// Two facts in one test, because they are two halves of the same contract. The renumbering must reach
    /// the existing row rather than insert a second one - <c>DbSet.Attach</c> read the zero key as unset
    /// and marked the page <c>Added</c>, after which the property flags had no update to narrow at all.
    /// </remarks>
    [Fact]
    public async Task TabOrderUpdate_OnADetachedKeyZeroPage_RenumbersTheRowAndWritesOnlyItsPosition()
    {
        int originalOrder;
        string? originalTitle;

        Tab detached;
        using (IServiceScope reading = _fixture.Services.CreateScope())
        {
            ITabRepository tabs = reading.ServiceProvider.GetRequiredService<ITabRepository>();
            Tab? read = await tabs.GetByIdAsync(ZeroKey);

            read.Should().NotBeNull();
            detached = read!;
        }

        originalOrder = detached.TabOrder;
        originalTitle = detached.Title;

        try
        {
            int before = await CountAsync("Tabs");

            detached.TabOrder = originalOrder + 100;
            detached.Title = FormattableString.Invariant($"Must not be written {Suffix()}");

            using (IServiceScope writing = _fixture.Services.CreateScope())
            {
                ITabRepository tabs = writing.ServiceProvider.GetRequiredService<ITabRepository>();
                IUnitOfWork unitOfWork = writing.ServiceProvider.GetRequiredService<IUnitOfWork>();

                await tabs.UpdateOrderAsync(detached);
                (await unitOfWork.SaveChangesAsync()).Should().Be(1);
            }

            (await CountAsync("Tabs")).Should().Be(
                before,
                "renumbering a page must not add one; the defective attach inserted a duplicate instead");

            using IServiceScope verifying = _fixture.Services.CreateScope();
            ITabRepository verifier = verifying.ServiceProvider.GetRequiredService<ITabRepository>();

            Tab? reread = await verifier.GetByIdAsync(ZeroKey);
            reread.Should().NotBeNull();
            reread!.TabOrder.Should().Be(originalOrder + 100, "the position is what this member writes");
            reread.Title.Should().Be(
                originalTitle,
                "the positional write covers four columns and the title is not one of them");
        }
        finally
        {
            await _fixture.Database.ExecuteAsync(
                "UPDATE [dbo].[Tabs] SET [TabOrder] = @tabOrder, [Title] = @title WHERE [TabID] = @tabId",
                new Dictionary<string, object?>
                {
                    ["tabOrder"] = originalOrder,
                    ["title"] = originalTitle,
                    ["tabId"] = ZeroKey,
                });
        }
    }

    /// <summary>
    /// Editing a page whose key is not zero leaves the page count unchanged even though the edit renumbers
    /// a sibling whose key is.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// THIS IS THE CASE THAT MADE THE DEFECT REACH ORDINARY USE, and it is the reason a repository-level
    /// fix alone would not have been enough evidence.
    /// </remarks>
    [Fact]
    public async Task TabServiceUpdate_OfANonZeroPage_DoesNotDuplicateItsZeroKeyedSibling()
    {
        _fixture.Seed.RootTabId.Should().Be(ZeroKey);
        _fixture.Seed.ChildTabId.Should().NotBe(ZeroKey, "the edited page must not be the zero-keyed one");

        int childTabId = _fixture.Seed.ChildTabId;
        TabDetailDto original;

        using (IServiceScope reading = _fixture.Services.CreateScope())
        {
            ITabService service = reading.ServiceProvider.GetRequiredService<ITabService>();
            Result<TabDetailDto?> read = await service.GetTabAsync(childTabId);

            read.IsSuccess.Should().BeTrue();
            read.Value.Should().NotBeNull();
            original = read.Value!;
        }

        int before = await CountAsync("Tabs");

        try
        {
            using (IServiceScope writing = _fixture.Services.CreateScope())
            {
                ITabService service = writing.ServiceProvider.GetRequiredService<ITabService>();

                Result<TabDetailDto> moved = await service.UpdateTabAsync(
                    childTabId,
                    RequestFrom(original, parentId: ZeroKey));

                moved.IsSuccess.Should().BeTrue(
                    FormattableString.Invariant($"the edit must succeed: {moved.Error}"));
                moved.Value.ParentId.Should().Be(ZeroKey);
            }

            (await CountAsync("Tabs")).Should().Be(
                before,
                "editing one page must not create another; the sibling positional write duplicated page 0");

            (await CountAsync("Tabs", "[TabID] = 0")).Should().Be(
                1,
                "the zero-keyed page is still a single row");
        }
        finally
        {
            using IServiceScope restoring = _fixture.Services.CreateScope();
            ITabService service = restoring.ServiceProvider.GetRequiredService<ITabService>();

            Result<TabDetailDto> restored = await service.UpdateTabAsync(
                childTabId,
                RequestFrom(original, original.ParentId));

            restored.IsSuccess.Should().BeTrue(
                FormattableString.Invariant($"the page must be restorable: {restored.Error}"));
        }

        (await CountAsync("Tabs")).Should().Be(
            before,
            "the restoring edit renumbers the same sibling band and must not add a page either");
    }

    /// <summary>Builds an update request that reproduces a page exactly, save for its parent.</summary>
    /// <param name="page">The page as it currently stands.</param>
    /// <param name="parentId">The parent the request should ask for.</param>
    /// <returns>A request carrying every current value and the requested parent.</returns>
    private static UpdateTabRequest RequestFrom(TabDetailDto page, int? parentId) => new()
    {
        TabName = page.TabName,
        Title = page.Title,
        Description = page.Description,
        Keywords = page.Keywords,
        ParentId = parentId,
        IsVisible = page.IsVisible,
        DisableLink = page.DisableLink,
        IconFile = page.IconFile,
        Url = page.Url,
        StartDate = page.StartDate,
        EndDate = page.EndDate,
        RefreshInterval = page.RefreshInterval,
        PageHeadText = page.PageHeadText,
        IsSecure = page.IsSecure,
        IsDeleted = page.IsDeleted,
    };

    /// <summary>Plants a tenant bearing the explicit key zero.</summary>
    /// <param name="portalName">The name the planted tenant carries.</param>
    /// <returns>A task that completes once the row exists.</returns>
    /// <remarks>
    /// Only the name has no store default among the tenant table's required columns, so one column is
    /// enough to produce a valid row. <c>IDENTITY_INSERT</c> is switched off again in the same batch so a
    /// failure cannot leave the session setting behind.
    /// </remarks>
    private Task PlantKeyZeroPortalAsync(string portalName) => _fixture.Database.ExecuteAsync(
        "SET IDENTITY_INSERT [dbo].[Portals] ON; "
        + "INSERT INTO [dbo].[Portals] ([PortalID], [PortalName]) VALUES (0, @portalName); "
        + "SET IDENTITY_INSERT [dbo].[Portals] OFF;",
        new Dictionary<string, object?> { ["portalName"] = portalName });

    /// <summary>Plants a role group bearing the explicit key zero, owned by the seeded tenant.</summary>
    /// <param name="roleGroupName">The name the planted group carries.</param>
    /// <returns>A task that completes once the row exists.</returns>
    private Task PlantKeyZeroRoleGroupAsync(string roleGroupName) => _fixture.Database.ExecuteAsync(
        "SET IDENTITY_INSERT [dbo].[RoleGroups] ON; "
        + "INSERT INTO [dbo].[RoleGroups] ([RoleGroupID], [PortalID], [RoleGroupName]) "
        + "VALUES (0, @portalId, @roleGroupName); "
        + "SET IDENTITY_INSERT [dbo].[RoleGroups] OFF;",
        new Dictionary<string, object?>
        {
            ["portalId"] = _fixture.Seed.PortalId,
            ["roleGroupName"] = roleGroupName,
        });

    /// <summary>Counts the rows of a mapped table.</summary>
    /// <param name="table">The table name, which is a literal at every call site.</param>
    /// <param name="predicate">An optional additional predicate, likewise a literal.</param>
    /// <returns>The row count.</returns>
    private Task<int> CountAsync(string table, string? predicate = null) =>
        _fixture.Database.ScalarAsync<int>(
            FormattableString.Invariant(
                $"SELECT COUNT(*) FROM [dbo].[{table}]{(predicate is null ? string.Empty : " WHERE " + predicate)}"));

    /// <summary>Reads a tenant's stored name straight from the row.</summary>
    /// <param name="portalId">The tenant to read.</param>
    /// <returns>The stored name.</returns>
    private Task<string> NameOfPortalAsync(int portalId) => _fixture.Database.ScalarAsync<string>(
        "SELECT [PortalName] FROM [dbo].[Portals] WHERE [PortalID] = @portalId",
        new Dictionary<string, object?> { ["portalId"] = portalId });

    /// <summary>Produces a short random suffix so concurrently executing suites cannot collide.</summary>
    /// <returns>A twelve-character suffix.</returns>
    private static string Suffix() => Guid.NewGuid().ToString("N")[..12];
}
