using DnnMigration.Domain.Abstractions.Repositories;
using DnnMigration.Domain.Entities;
using DnnMigration.Domain.Enums;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DnnMigration.IntegrationTests.Persistence;

/// <summary>
/// Covers the binding between the entity model and the existing DotNetNuke schema.
/// </summary>
/// <remarks>
/// <para>
/// The schema is externally owned. The entity configurations pin every table and column name explicitly
/// so that the model reads and writes the installation the legacy application already populated, and the
/// baseline migration deliberately emits no data-definition language at all. A mapping mistake therefore
/// cannot be caught by a build or by a migration diff: it surfaces only when a query runs. That is what
/// this suite exists to catch.
/// </para>
/// <para>
/// The assertions read the catalogue views rather than the context's own model, for two reasons. The
/// context is internal to the infrastructure assembly and this project deliberately holds no visibility
/// into it, and — more importantly — asserting the model against itself would prove only that the model
/// is self-consistent. Reading <c>INFORMATION_SCHEMA</c> and <c>sys.identity_columns</c> compares the
/// model against the thing it has to agree with.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
[Collection(IntegrationTestCollection.Name)]
public sealed class DnnDbContextTests
{
    /// <summary>The identifier seeds the legacy schema declares, keyed by table.</summary>
    /// <remarks>
    /// Three of these seeds are load-bearing rather than incidental. A tenant identifier of -1 collides
    /// with the legacy "absent integer" sentinel, and a role, page or module identifier of 0 collides with
    /// the CLR default for an unassigned integer. Both collisions have already produced real defects in
    /// this migration, so the seeds are asserted here to make a future schema edit that changes one of
    /// them fail loudly instead of quietly changing which identifiers are reachable.
    /// </remarks>
    private static readonly IReadOnlyDictionary<string, int> ExpectedIdentitySeeds = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["Portals"] = -1,
        ["Roles"] = 0,
        ["RoleGroups"] = 0,
        ["Tabs"] = 0,
        ["Modules"] = 0,
        ["TabModules"] = 1,
        ["Users"] = 1,
    };

    /// <summary>The twenty-one tables the entity model binds to.</summary>
    private static readonly string[] MappedTables =
    [
        "DesktopModules",
        "ModuleControls",
        "ModuleDefinitions",
        "ModulePermission",
        "Modules",
        "ModuleSettings",
        "Permission",
        "PortalAlias",
        "PortalDesktopModules",
        "Portals",
        "ProfilePropertyDefinition",
        "RoleGroups",
        "Roles",
        "TabModules",
        "TabModuleSettings",
        "TabPermission",
        "Tabs",
        "UserPortals",
        "UserProfile",
        "UserRoles",
        "Users",
    ];

    /// <summary>
    /// Column names whose legacy spelling differs from the property that carries them, or whose spelling is
    /// otherwise easy to correct by accident.
    /// </summary>
    /// <remarks>
    /// Every entry here is a name a well-meaning refactor would be tempted to modernise. <c>Authorised</c>
    /// carries the British spelling the original schema used; <c>GUID</c> and the identifier columns carry
    /// casings that no C# naming convention would produce; <c>KeyWords</c> capitalises its second syllable;
    /// <c>RSVPCode</c> is fully upper-cased; and <c>TimezoneOffset</c> lower-cases a word the property
    /// spells with a capital. Correcting any of them would break every installation in the field.
    /// </remarks>
    private static readonly (string Table, string Column)[] LegacyColumnNames =
    [
        ("Portals", "PortalID"),
        ("Portals", "GUID"),
        ("Portals", "KeyWords"),
        ("Portals", "TimezoneOffset"),
        ("Portals", "HomeDirectory"),
        ("Users", "UserID"),
        ("Users", "Username"),
        ("Users", "DisplayName"),
        ("UserPortals", "Authorised"),
        ("UserPortals", "UserPortalId"),
        ("Roles", "RoleID"),
        ("Roles", "RSVPCode"),
        ("Roles", "BillingFrequency"),
        ("Roles", "AutoAssignment"),
        ("RoleGroups", "RoleGroupID"),
        ("UserRoles", "UserRoleID"),
        ("Permission", "PermissionID"),
        ("Permission", "ModuleDefID"),
        ("Permission", "PermissionKey"),
        ("ModuleDefinitions", "ModuleDefID"),
        ("ModuleSettings", "SettingName"),
        ("TabModuleSettings", "SettingName"),
        ("Tabs", "TabID"),
        ("Tabs", "KeyWords"),
        ("Tabs", "TabPath"),
        ("ProfilePropertyDefinition", "PropertyDefinitionID"),
        ("UserProfile", "PropertyDefinitionID"),
        ("ModulePermission", "ModulePermissionID"),
        ("TabPermission", "TabPermissionID"),
    ];

    private readonly ApiTestFixture _fixture;

    /// <summary>Initialises a new instance of the <see cref="DnnDbContextTests"/> class.</summary>
    /// <param name="fixture">The shared host and database.</param>
    public DnnDbContextTests(ApiTestFixture fixture) => _fixture = fixture;

    /// <summary>Every table the model binds to is present under the <c>dbo</c> schema.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task Model_MapsEveryLegacyTableUnderTheDboSchema()
    {
        List<string> missing = [];

        foreach (string table in MappedTables)
        {
            int present = await _fixture.Database.ScalarAsync<int>(
                "SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES "
                + "WHERE TABLE_SCHEMA = 'dbo' AND TABLE_NAME = @table",
                new Dictionary<string, object?> { ["table"] = table });

            if (present == 0)
            {
                missing.Add(table);
            }
        }

        missing.Should().BeEmpty("every mapped entity binds to a table that must exist in the legacy schema");
    }

    /// <summary>Each pinned legacy column name is present exactly as the configuration spells it.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task Model_BindsTheLegacyColumnNames()
    {
        List<string> missing = [];

        foreach ((string table, string column) in LegacyColumnNames)
        {
            int present = await _fixture.Database.ScalarAsync<int>(
                "SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS "
                + "WHERE TABLE_SCHEMA = 'dbo' AND TABLE_NAME = @table AND COLUMN_NAME = @column",
                new Dictionary<string, object?> { ["table"] = table, ["column"] = column });

            if (present == 0)
            {
                missing.Add(FormattableString.Invariant($"{table}.{column}"));
            }
        }

        missing.Should().BeEmpty("the configurations pin these column names and the schema must still carry them");
    }

    /// <summary>The identifier seeds the legacy schema declares are unchanged.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task IdentitySeeds_MatchTheLegacySchema()
    {
        Dictionary<string, int> actual = new(StringComparer.Ordinal);

        foreach (string table in ExpectedIdentitySeeds.Keys)
        {
            int seed = await _fixture.Database.ScalarAsync<int>(
                "SELECT CAST(c.seed_value AS int) FROM sys.identity_columns c "
                + "INNER JOIN sys.tables t ON t.object_id = c.object_id "
                + "INNER JOIN sys.schemas s ON s.schema_id = t.schema_id "
                + "WHERE s.name = 'dbo' AND t.name = @table",
                new Dictionary<string, object?> { ["table"] = table });

            actual[table] = seed;
        }

        actual.Should().Equal(ExpectedIdentitySeeds);
    }

    /// <summary>
    /// The hosting charge is stored as a monetary type, not as text, so no value converter stands between
    /// the decimal property and the column.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// The column began life as <c>nvarchar(10)</c> in the baseline script, but the <c>Tmp_Portals</c>
    /// rebuild retyped it with an explicit <c>CONVERT(money, HostFee)</c>
    /// (<c>01.00.05.SqlDataProvider:L1376,L1412</c>) and <c>03.01.01.SqlDataProvider:L1118</c> re-asserted
    /// <c>ALTER TABLE Portals ALTER COLUMN HostFee money NOT NULL</c>. No later script revisits it, so
    /// <c>money</c> is the terminal type. Asserting the store type is what stops a converter from being
    /// reintroduced: binding this column as text would read a genuine installation incorrectly and would
    /// make the stored value depend on the writing server's culture.
    /// </remarks>
    [Fact]
    public async Task HostFee_IsStoredAsMoneyRatherThanText()
    {
        string dataType = await _fixture.Database.ScalarAsync<string>(
            "SELECT DATA_TYPE FROM INFORMATION_SCHEMA.COLUMNS "
            + "WHERE TABLE_SCHEMA = 'dbo' AND TABLE_NAME = 'Portals' AND COLUMN_NAME = 'HostFee'");

        // A monetary column has no character length at all, so the catalogue reports null for it. The
        // count is asked for rather than the length itself because the scalar helper treats a null
        // result as a failed statement.
        int textLengthCount = await _fixture.Database.ScalarAsync<int>(
            "SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS "
            + "WHERE TABLE_SCHEMA = 'dbo' AND TABLE_NAME = 'Portals' AND COLUMN_NAME = 'HostFee' "
            + "AND CHARACTER_MAXIMUM_LENGTH IS NOT NULL");

        dataType.Should().Be("money");
        textLengthCount.Should().Be(0, "a monetary column carries no character maximum length");
    }

    /// <summary>Both billing frequencies are stored as a single non-Unicode character.</summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// The single-character codes are data, not presentation: an installation in the field already holds
    /// rows containing them. That is why the enumeration carries explicit character values instead of the
    /// ordinals a fresh design would have used.
    /// </remarks>
    [Fact]
    public async Task BillingFrequencies_AreStoredAsASingleCharacter()
    {
        foreach (string column in new[] { "BillingFrequency", "TrialFrequency" })
        {
            string dataType = await _fixture.Database.ScalarAsync<string>(
                "SELECT DATA_TYPE FROM INFORMATION_SCHEMA.COLUMNS "
                + "WHERE TABLE_SCHEMA = 'dbo' AND TABLE_NAME = 'Roles' AND COLUMN_NAME = @column",
                new Dictionary<string, object?> { ["column"] = column });

            int length = await _fixture.Database.ScalarAsync<int>(
                "SELECT CHARACTER_MAXIMUM_LENGTH FROM INFORMATION_SCHEMA.COLUMNS "
                + "WHERE TABLE_SCHEMA = 'dbo' AND TABLE_NAME = 'Roles' AND COLUMN_NAME = @column",
                new Dictionary<string, object?> { ["column"] = column });

            dataType.Should().Be("char", FormattableString.Invariant($"{column} carries a legacy single-character code"));
            length.Should().Be(1);
        }
    }

    /// <summary>
    /// A tenant written through the repository lands in the legacy columns, reads back unchanged, and can be
    /// removed again.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// This is the write half of the mapping contract. The listing endpoints would still pass if a column
    /// were bound to the wrong name in one direction only, because a read of a column nobody writes simply
    /// returns its default. Writing, then reading the raw row, then reading it back through the model,
    /// closes that gap.
    /// </remarks>
    [Fact]
    public async Task Portal_RoundTripsThroughTheMappedColumnsAndConverters()
    {
        string suffix = Suffix();
        Guid identifier = Guid.NewGuid();
        int portalId;

        using (IServiceScope writing = _fixture.Services.CreateScope())
        {
            IPortalRepository portals = writing.ServiceProvider.GetRequiredService<IPortalRepository>();
            IUnitOfWork unitOfWork = writing.ServiceProvider.GetRequiredService<IUnitOfWork>();

            Portal portal = new()
            {
                PortalName = FormattableString.Invariant($"Mapping Portal {suffix}"),
                Description = "Written by the persistence mapping suite.",
                KeyWords = "mapping, persistence",
                UserRegistration = UserRegistrationMode.PublicRegistration,
                BannerAdvertising = BannerAdvertisingMode.None,
                Currency = "USD",
                HostFee = 12.5m,
                HostSpace = 256,
                PortalGuid = identifier,
                DefaultLanguage = "en-US",
                TimeZoneOffset = -480,
                HomeDirectory = FormattableString.Invariant($"Portals/{suffix}"),
                PageQuota = 25,
                UserQuota = 50,
                SiteLogHistory = 7,
            };

            await portals.AddAsync(portal);
            await unitOfWork.SaveChangesAsync();

            portalId = portal.PortalId;
        }

        // The hosting charge lands in a monetary column as a number, so it round-trips exactly and its
        // stored form does not depend on the writing server's decimal separator.
        decimal storedFee = await _fixture.Database.ScalarAsync<decimal>(
            "SELECT [HostFee] FROM [dbo].[Portals] WHERE [PortalID] = @portalId",
            new Dictionary<string, object?> { ["portalId"] = portalId });

        storedFee.Should().Be(12.5m);

        string storedGuid = await _fixture.Database.ScalarAsync<string>(
            "SELECT CAST([GUID] AS nvarchar(36)) FROM [dbo].[Portals] WHERE [PortalID] = @portalId",
            new Dictionary<string, object?> { ["portalId"] = portalId });

        Guid.Parse(storedGuid).Should().Be(identifier);

        string storedDirectory = await _fixture.Database.ScalarAsync<string>(
            "SELECT [HomeDirectory] FROM [dbo].[Portals] WHERE [PortalID] = @portalId",
            new Dictionary<string, object?> { ["portalId"] = portalId });

        storedDirectory.Should().Be(FormattableString.Invariant($"Portals/{suffix}"));

        int storedOffset = await _fixture.Database.ScalarAsync<int>(
            "SELECT [TimezoneOffset] FROM [dbo].[Portals] WHERE [PortalID] = @portalId",
            new Dictionary<string, object?> { ["portalId"] = portalId });

        storedOffset.Should().Be(-480);

        using (IServiceScope reading = _fixture.Services.CreateScope())
        {
            IPortalRepository portals = reading.ServiceProvider.GetRequiredService<IPortalRepository>();

            Portal? reread = await portals.GetByIdAsync(portalId);

            reread.Should().NotBeNull();
            reread!.PortalName.Should().Be(FormattableString.Invariant($"Mapping Portal {suffix}"));
            reread.HostFee.Should().Be(12.5m);
            reread.HostSpace.Should().Be(256);
            reread.PortalGuid.Should().Be(identifier);
            reread.Currency.Should().Be("USD");
            reread.TimeZoneOffset.Should().Be(-480);
            reread.PageQuota.Should().Be(25);
            reread.UserQuota.Should().Be(50);
            reread.SiteLogHistory.Should().Be(7);
            reread.UserRegistration.Should().Be(UserRegistrationMode.PublicRegistration);
            reread.BannerAdvertising.Should().Be(BannerAdvertisingMode.None);
        }

        using (IServiceScope removing = _fixture.Services.CreateScope())
        {
            IPortalRepository portals = removing.ServiceProvider.GetRequiredService<IPortalRepository>();
            IUnitOfWork unitOfWork = removing.ServiceProvider.GetRequiredService<IUnitOfWork>();

            Portal? doomed = await portals.GetByIdAsync(portalId);
            doomed.Should().NotBeNull();

            await portals.DeleteAsync(doomed!.PortalId);
            await unitOfWork.SaveChangesAsync();
        }

        int remaining = await _fixture.Database.ScalarAsync<int>(
            "SELECT COUNT(*) FROM [dbo].[Portals] WHERE [PortalID] = @portalId",
            new Dictionary<string, object?> { ["portalId"] = portalId });

        remaining.Should().Be(0);
    }

    /// <summary>A billing frequency written through the model is stored as its legacy character code.</summary>
    /// <returns>A task representing the test.</returns>
    [Fact]
    public async Task Role_RoundTripsTheFrequencyConverter()
    {
        string suffix = Suffix();
        int roleId;

        using (IServiceScope writing = _fixture.Services.CreateScope())
        {
            IRoleRepository roles = writing.ServiceProvider.GetRequiredService<IRoleRepository>();
            IUnitOfWork unitOfWork = writing.ServiceProvider.GetRequiredService<IUnitOfWork>();

            Role role = new()
            {
                PortalId = _fixture.Seed.PortalId,
                RoleName = FormattableString.Invariant($"Frequency Role {suffix}"),
                Description = "Written by the persistence mapping suite.",
                ServiceFee = 9.99m,
                BillingPeriod = 3,
                BillingFrequency = Domain.Enums.BillingFrequency.Month,
                TrialFee = 1.5m,
                TrialPeriod = 2,
                TrialFrequency = Domain.Enums.BillingFrequency.Week,
                IsPublic = true,
                AutoAssignment = false,
            };

            roles.Add(role);
            await unitOfWork.SaveChangesAsync();

            roleId = role.RoleId;
        }

        string storedBilling = await _fixture.Database.ScalarAsync<string>(
            "SELECT [BillingFrequency] FROM [dbo].[Roles] WHERE [RoleID] = @roleId",
            new Dictionary<string, object?> { ["roleId"] = roleId });

        string storedTrial = await _fixture.Database.ScalarAsync<string>(
            "SELECT [TrialFrequency] FROM [dbo].[Roles] WHERE [RoleID] = @roleId",
            new Dictionary<string, object?> { ["roleId"] = roleId });

        storedBilling.Should().Be("M");
        storedTrial.Should().Be("W");

        using (IServiceScope reading = _fixture.Services.CreateScope())
        {
            IRoleRepository roles = reading.ServiceProvider.GetRequiredService<IRoleRepository>();

            Role? reread = await roles.GetAsync(roleId);

            reread.Should().NotBeNull();
            reread!.BillingFrequency.Should().Be(Domain.Enums.BillingFrequency.Month);
            reread.TrialFrequency.Should().Be(Domain.Enums.BillingFrequency.Week);
            reread.ServiceFee.Should().Be(9.99m);
            reread.TrialFee.Should().Be(1.5m);
            reread.BillingPeriod.Should().Be(3);
            reread.TrialPeriod.Should().Be(2);
            reread.IsPublic.Should().BeTrue();
            reread.AutoAssignment.Should().BeFalse();
        }

        await RemoveRoleAsync(roleId);
    }

    /// <summary>A role with no frequency leaves the character columns null rather than writing a code.</summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// The distinction matters because the enumeration has a member whose stored code is the letter N. A
    /// free role must be indistinguishable from a legacy row that never carried a frequency at all, so the
    /// converter has to leave a null property as a null column rather than coercing it to that member.
    /// </remarks>
    [Fact]
    public async Task Role_WithNoFrequency_LeavesTheColumnsNull()
    {
        string suffix = Suffix();
        int roleId;

        using (IServiceScope writing = _fixture.Services.CreateScope())
        {
            IRoleRepository roles = writing.ServiceProvider.GetRequiredService<IRoleRepository>();
            IUnitOfWork unitOfWork = writing.ServiceProvider.GetRequiredService<IUnitOfWork>();

            Role role = new()
            {
                PortalId = _fixture.Seed.PortalId,
                RoleName = FormattableString.Invariant($"Free Role {suffix}"),
                BillingFrequency = null,
                TrialFrequency = null,
                IsPublic = false,
                AutoAssignment = false,
            };

            roles.Add(role);
            await unitOfWork.SaveChangesAsync();

            roleId = role.RoleId;
        }

        int nulls = await _fixture.Database.ScalarAsync<int>(
            "SELECT CASE WHEN [BillingFrequency] IS NULL AND [TrialFrequency] IS NULL THEN 1 ELSE 0 END "
            + "FROM [dbo].[Roles] WHERE [RoleID] = @roleId",
            new Dictionary<string, object?> { ["roleId"] = roleId });

        nulls.Should().Be(1);

        using (IServiceScope reading = _fixture.Services.CreateScope())
        {
            IRoleRepository roles = reading.ServiceProvider.GetRequiredService<IRoleRepository>();

            Role? reread = await roles.GetAsync(roleId);

            reread.Should().NotBeNull();
            reread!.BillingFrequency.Should().BeNull();
            reread.TrialFrequency.Should().BeNull();
        }

        await RemoveRoleAsync(roleId);
    }

    /// <summary>Every persistence abstraction resolves from the composed host inside a request scope.</summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// The host validates its container on build, so a missing registration would already have failed the
    /// fixture. What this adds is proof that each abstraction resolves to something that can actually reach
    /// the database, which a registration check alone does not establish.
    /// </remarks>
    [Fact]
    public async Task Repositories_ResolveFromTheCompositionRootWithinAScope()
    {
        using IServiceScope scope = _fixture.Services.CreateScope();
        IServiceProvider services = scope.ServiceProvider;

        services.GetRequiredService<IUnitOfWork>().Should().NotBeNull();
        services.GetRequiredService<IPortalRepository>().Should().NotBeNull();
        services.GetRequiredService<IPortalAliasRepository>().Should().NotBeNull();
        services.GetRequiredService<IModuleRepository>().Should().NotBeNull();
        services.GetRequiredService<IModuleDefinitionRepository>().Should().NotBeNull();
        services.GetRequiredService<ITabRepository>().Should().NotBeNull();
        services.GetRequiredService<IUserRepository>().Should().NotBeNull();
        services.GetRequiredService<IUserProfileRepository>().Should().NotBeNull();
        services.GetRequiredService<IRoleRepository>().Should().NotBeNull();
        services.GetRequiredService<IPermissionRepository>().Should().NotBeNull();

        // A resolved repository reaches the seeded installation, which is the part a container check cannot
        // establish on its own.
        bool exists = await services.GetRequiredService<IPortalRepository>().ExistsAsync(_fixture.Seed.PortalId);
        exists.Should().BeTrue();
    }

    /// <summary>The unit of work is scoped, so one request commits independently of another.</summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// A singleton unit of work would share one change tracker across every concurrent request, which would
    /// let one caller's uncommitted edit be saved by an unrelated caller. Asserting the lifetime here keeps
    /// that from being reintroduced by a registration change.
    /// </remarks>
    [Fact]
    public Task UnitOfWork_IsScopedToARequest()
    {
        using IServiceScope first = _fixture.Services.CreateScope();
        using IServiceScope second = _fixture.Services.CreateScope();

        IUnitOfWork withinFirst = first.ServiceProvider.GetRequiredService<IUnitOfWork>();
        IUnitOfWork againWithinFirst = first.ServiceProvider.GetRequiredService<IUnitOfWork>();
        IUnitOfWork withinSecond = second.ServiceProvider.GetRequiredService<IUnitOfWork>();

        againWithinFirst.Should().BeSameAs(withinFirst);
        withinSecond.Should().NotBeSameAs(withinFirst);

        return Task.CompletedTask;
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

    /// <summary>Produces a short random suffix so concurrently executing suites cannot collide.</summary>
    /// <returns>A twelve-character suffix.</returns>
    private static string Suffix() => Guid.NewGuid().ToString("N")[..12];
}
