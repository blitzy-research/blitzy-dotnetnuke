using DnnMigration.Domain.Abstractions.Repositories;
using DnnMigration.Domain.Entities;
using DnnMigration.Domain.Enums;
using DnnMigration.Infrastructure;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.Configuration;
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
/// <strong>Two layers, and each catches what the other cannot.</strong> The suite asserts the mapping
/// twice, from opposite directions, because a single direction leaves a real gap open.
/// </para>
/// <list type="number">
///   <item><description>
///     <strong>Catalogue assertions</strong> read <c>INFORMATION_SCHEMA</c> and <c>sys.identity_columns</c>
///     on the provisioned database, then round-trip rows through the repositories. This compares the model
///     against the thing it has to agree with, which asserting the model against itself can never do: a
///     model that is internally consistent and uniformly wrong would satisfy every metadata assertion.
///   </description></item>
///   <item><description>
///     <strong>Model-metadata assertions</strong> read the composed <see cref="IModel"/> directly. These
///     cover the facts that leave no trace in a catalogue at all, and there are three classes of them. A
///     deliberately UNMAPPED property is invisible to <c>INFORMATION_SCHEMA</c> - an ignored property and a
///     misspelled one look identical from the database, namely absent - so the eleven externally stored
///     membership properties can only be asserted here. The COUNT of mapped entity types is likewise a
///     model fact: the database carries tables this migration deliberately does not map, so counting
///     catalogue rows would answer a different question. And an entity that OUGHT NOT to exist, such as the
///     ambient portal-settings composite, cannot be shown absent by querying for a table that was never
///     going to be there.
///   </description></item>
/// </list>
/// <para>
/// <strong>The context type is never named, and nothing here makes it nameable.</strong> The context is
/// <c>internal</c> to the infrastructure assembly by design, and this project holds no visibility into it -
/// no <c>InternalsVisibleTo</c>, no assembly attribute, no request for the type to be made public. The
/// model is nonetheless reachable through entirely public API, and <see cref="ComposeModel"/> documents
/// how. That accessibility is the design rather than an obstacle to it.
/// </para>
/// <para>
/// MIGRATION: no schema is created, altered or dropped from this file. <c>EnsureCreated</c>,
/// <c>EnsureDeleted</c> and <c>Database.Migrate</c> are absent and must stay absent - the terminal
/// DotNetNuke schema depends on membership objects the eighty-eight legacy upgrade scripts only ever ALTER,
/// so no model-driven creation can reproduce it even in principle.
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

    /// <summary>
    /// Model relationships that deliberately have no physical foreign-key constraint in the terminal schema.
    /// </summary>
    /// <remarks>
    /// The profile-definition relationship is queryable over its nullable <c>ModuleDefID</c> column but the
    /// legacy upgrade chain never constrained it. The two permission-role relationships are likewise
    /// queryable, while their columns must admit the negative pseudo-role identifiers the database uses.
    /// These relationships therefore stay in the EF model with <c>NoAction</c> but are excluded from the
    /// physical constraint inventory below.
    /// </remarks>
    private static readonly IReadOnlySet<string> ConceptualOnlyForeignKeys = new HashSet<string>(StringComparer.Ordinal)
    {
        "FK_ProfilePropertyDefinition_ModuleDefinitions_ModuleDefID",
        "FK_ModulePermission_Roles_RoleID",
        "FK_TabPermission_Roles_RoleID",
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

    /// <summary>
    /// Connection string used solely to compose the model. It is deliberately unreachable and nothing ever
    /// opens it.
    /// </summary>
    /// <remarks>
    /// Composing a model requires a configured provider but NOT a reachable server: Entity Framework builds
    /// the model from the entity configurations alone and connects only when a query executes. The host name
    /// uses the reserved <c>.invalid</c> top-level domain so that a future edit which accidentally opens a
    /// connection fails immediately and unmistakably instead of reaching some real server. It carries no
    /// credential of any kind, which is the other reason it is stated here in full rather than borrowed from
    /// the fixture's live connection string.
    /// </remarks>
    private const string ModelOnlyConnectionString =
        "Server=model-composition-only.invalid;Database=DnnMigrationModelOnly;Integrated Security=true";

    /// <summary>Schema every mapped entity is bound to.</summary>
    /// <remarks>
    /// <c>Website/release.config:L355</c> declares <c>databaseOwner="dbo"</c> and <c>L354</c> declares
    /// <c>objectQualifier=""</c>, so the mapping targets un-prefixed names owned by <c>dbo</c>. The
    /// qualifier remains configurable for an installation that uses one; this asserts the value this
    /// migration actually binds.
    /// </remarks>
    private const string LegacySchema = "dbo";

    /// <summary>The composed entity model, read once for the whole class.</summary>
    /// <remarks>
    /// <see cref="IModel"/> is immutable and is cached by the framework independently of the context
    /// instance that exposed it, so holding it after the composing scope and provider are disposed is safe
    /// and avoids paying for composition twenty-one times over a theory.
    /// </remarks>
    private static readonly IModel Model = ComposeModel();

    /// <summary>Every mapped entity paired with the legacy table it binds to.</summary>
    /// <remarks>
    /// <para>
    /// The single source of truth for the entity-to-table mapping, projected into theory data below so the
    /// count and the individual mappings cannot disagree with one another.
    /// </para>
    /// <para>
    /// SIX OF THESE TABLE NAMES ARE SINGULAR while their sets are plural, and one does not match its entity
    /// name at all: <c>UserProfileValue</c> binds to <c>UserProfile</c>. Pluralising any of them would read
    /// a genuine installation as an empty one, and no compiler or migration diff would notice.
    /// </para>
    /// </remarks>
    private static readonly (Type Entity, string Table)[] MappedEntityTables =
    [
        (typeof(Portal), "Portals"),
        (typeof(PortalAlias), "PortalAlias"),
        (typeof(Module), "Modules"),
        (typeof(ModuleDefinition), "ModuleDefinitions"),
        (typeof(ModuleControl), "ModuleControls"),
        (typeof(ModuleSetting), "ModuleSettings"),
        (typeof(DesktopModule), "DesktopModules"),
        (typeof(PortalDesktopModule), "PortalDesktopModules"),
        (typeof(Tab), "Tabs"),
        (typeof(TabModule), "TabModules"),
        (typeof(TabModuleSetting), "TabModuleSettings"),
        (typeof(User), "Users"),
        (typeof(UserPortal), "UserPortals"),
        (typeof(UserProfileValue), "UserProfile"),
        (typeof(ProfilePropertyDefinition), "ProfilePropertyDefinition"),
        (typeof(Role), "Roles"),
        (typeof(RoleGroup), "RoleGroups"),
        (typeof(UserRole), "UserRoles"),
        (typeof(Permission), "Permission"),
        (typeof(ModulePermission), "ModulePermission"),
        (typeof(TabPermission), "TabPermission"),
    ];

    /// <summary>The table names that stayed singular while their sets were pluralised.</summary>
    private static readonly (Type Entity, string Table)[] SingularLegacyTables =
    [
        (typeof(PortalAlias), "PortalAlias"),
        (typeof(UserProfileValue), "UserProfile"),
        (typeof(ProfilePropertyDefinition), "ProfilePropertyDefinition"),
        (typeof(Permission), "Permission"),
        (typeof(ModulePermission), "ModulePermission"),
        (typeof(TabPermission), "TabPermission"),
    ];

    private readonly ApiTestFixture _fixture;

    /// <summary>Initialises a new instance of the <see cref="DnnDbContextTests"/> class.</summary>
    /// <param name="fixture">The shared host and database.</param>
    public DnnDbContextTests(ApiTestFixture fixture) => _fixture = fixture;

    /// <summary>Every mapped entity with its legacy table, as theory data.</summary>
    public static TheoryData<Type, string> MappedEntities
    {
        get
        {
            TheoryData<Type, string> data = new();

            foreach ((Type entity, string table) in MappedEntityTables)
            {
                data.Add(entity, table);
            }

            return data;
        }
    }

    /// <summary>
    /// Property-to-column pairs whose legacy spelling a well-meaning refactor would be tempted to
    /// modernise.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The legacy schema is not internally consistent, and that is the point of this table.</strong>
    /// Most identifier columns end in an upper-cased <c>ID</c> - <c>Users.UserID</c>, <c>Portals.PortalID</c>
    /// - but a significant minority do not, and the SAME logical column is spelled differently depending on
    /// which table carries it: <c>Users.UserID</c> is upper-cased while <c>UserPortals.UserId</c> is not, and
    /// <c>Portals.PortalID</c> is upper-cased while <c>UserPortals.PortalId</c> is not. A configuration
    /// written by pattern rather than by measurement gets those wrong, and nothing but a query failure
    /// against a real installation would reveal it.
    /// </para>
    /// <para>
    /// Five pairs do not merely differ in casing but rename outright: <c>PortalGuid</c> is stored as
    /// <c>GUID</c>, <c>TimeZoneOffset</c> as <c>TimezoneOffset</c>, <c>HttpAlias</c> as <c>HTTPAlias</c>,
    /// <c>IsAuthorised</c> as <c>Authorised</c> - the original British spelling, with the boolean prefix
    /// dropped - and <c>ModuleDefinitionId</c> as the abbreviated <c>ModuleDefID</c>.
    /// </para>
    /// <para>
    /// The property side uses <c>nameof</c> throughout so that renaming a property is a compile error here
    /// rather than a test that keeps passing while asserting nothing.
    /// </para>
    /// </remarks>
    public static TheoryData<Type, string, string> LegacyColumnSpellings
    {
        get
        {
            TheoryData<Type, string, string> data = new();

            // The upper-cased ID family: the property spells it Id, the column spells it ID.
            data.Add(typeof(Portal), nameof(Portal.PortalId), "PortalID");
            data.Add(typeof(PortalAlias), nameof(PortalAlias.PortalAliasId), "PortalAliasID");
            data.Add(typeof(PortalAlias), nameof(PortalAlias.PortalId), "PortalID");
            data.Add(typeof(Module), nameof(Module.ModuleId), "ModuleID");
            data.Add(typeof(ModuleControl), nameof(ModuleControl.ModuleControlId), "ModuleControlID");
            data.Add(typeof(ModuleSetting), nameof(ModuleSetting.ModuleId), "ModuleID");
            data.Add(typeof(DesktopModule), nameof(DesktopModule.DesktopModuleId), "DesktopModuleID");
            data.Add(
                typeof(PortalDesktopModule),
                nameof(PortalDesktopModule.PortalDesktopModuleId),
                "PortalDesktopModuleID");
            data.Add(typeof(Tab), nameof(Tab.TabId), "TabID");
            data.Add(typeof(TabModule), nameof(TabModule.TabModuleId), "TabModuleID");
            data.Add(typeof(TabModuleSetting), nameof(TabModuleSetting.TabModuleId), "TabModuleID");
            data.Add(typeof(User), nameof(User.UserId), "UserID");
            data.Add(typeof(UserProfileValue), nameof(UserProfileValue.ProfileId), "ProfileID");
            data.Add(
                typeof(ProfilePropertyDefinition),
                nameof(ProfilePropertyDefinition.PropertyDefinitionId),
                "PropertyDefinitionID");
            data.Add(typeof(Role), nameof(Role.RoleId), "RoleID");
            data.Add(typeof(Role), nameof(Role.PortalId), "PortalID");
            data.Add(typeof(RoleGroup), nameof(RoleGroup.RoleGroupId), "RoleGroupID");
            data.Add(typeof(UserRole), nameof(UserRole.UserRoleId), "UserRoleID");
            data.Add(typeof(Permission), nameof(Permission.PermissionId), "PermissionID");
            data.Add(
                typeof(ModulePermission),
                nameof(ModulePermission.ModulePermissionId),
                "ModulePermissionID");
            data.Add(typeof(TabPermission), nameof(TabPermission.TabPermissionId), "TabPermissionID");

            // The exceptions to that family: identifier columns the legacy schema spells in mixed case.
            // UserPortals disagrees with BOTH Users and Portals about how to spell the keys it joins on.
            data.Add(typeof(UserPortal), nameof(UserPortal.UserId), "UserId");
            data.Add(typeof(UserPortal), nameof(UserPortal.PortalId), "PortalId");
            data.Add(typeof(UserPortal), nameof(UserPortal.UserPortalId), "UserPortalId");
            data.Add(typeof(Tab), nameof(Tab.ParentId), "ParentId");
            data.Add(typeof(User), nameof(User.AffiliateId), "AffiliateId");
            data.Add(typeof(Portal), nameof(Portal.AdministratorId), "AdministratorId");
            data.Add(typeof(Portal), nameof(Portal.AdministratorRoleId), "AdministratorRoleId");
            data.Add(typeof(Portal), nameof(Portal.RegisteredRoleId), "RegisteredRoleId");
            data.Add(typeof(Portal), nameof(Portal.HomeTabId), "HomeTabId");

            // Outright renames, not casing differences.
            data.Add(typeof(Portal), nameof(Portal.PortalGuid), "GUID");
            data.Add(typeof(Portal), nameof(Portal.TimeZoneOffset), "TimezoneOffset");
            data.Add(typeof(PortalAlias), nameof(PortalAlias.HttpAlias), "HTTPAlias");
            data.Add(typeof(UserPortal), nameof(UserPortal.IsAuthorised), "Authorised");
            data.Add(
                typeof(ModuleDefinition),
                nameof(ModuleDefinition.ModuleDefinitionId),
                "ModuleDefID");

            // Non-identifier columns whose casing no C# convention would produce.
            data.Add(typeof(Portal), nameof(Portal.KeyWords), "KeyWords");
            data.Add(typeof(UserProfileValue), nameof(UserProfileValue.LastUpdatedDate), "LastUpdatedDate");

            return data;
        }
    }

    /// <summary>
    /// Properties the account aggregate carries but the mapping deliberately leaves to the external
    /// membership store.
    /// </summary>
    /// <remarks>
    /// These eleven live in the externally installed <c>aspnet_*</c> tables and are composed by the user
    /// repository rather than mapped as columns on <c>dbo.Users</c>. MIGRATION: the legacy DDL chain only
    /// ever ALTERs those objects - searching all eighty-eight scripts case-insensitively and across every
    /// naming form they use finds exactly one CREATE of an <c>aspnet_</c> object, and it is a DotNetNuke
    /// helper procedure rather than one of Microsoft's - so the membership store is an external dependency
    /// the account entity maps alongside, never something this model owns.
    /// </remarks>
    public static TheoryData<string> ExternallyStoredAccountProperties
    {
        get
        {
            TheoryData<string> data = new();

            data.Add(nameof(User.IsApproved));
            data.Add(nameof(User.CreatedDate));
            data.Add(nameof(User.IsOnline));
            data.Add(nameof(User.LastActivityDate));
            data.Add(nameof(User.LastLockoutDate));
            data.Add(nameof(User.LastLoginDate));
            data.Add(nameof(User.LastPasswordChangeDate));
            data.Add(nameof(User.IsLockedOut));
            data.Add(nameof(User.PasswordHash));
            data.Add(nameof(User.PasswordAnswer));
            data.Add(nameof(User.PasswordQuestion));

            return data;
        }
    }

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
    /// Every mapped non-primary index has the model's physical name, ordered columns, uniqueness and filter.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// This is intentionally an inventory comparison rather than a handful of spot checks. The earlier
    /// embedded schema had thirty-seven indexes against the model's thirty-three: eight existed only in the
    /// test database, four were absent there, fourteen matching structures carried different names, and three
    /// unique indexes added filters the terminal schema does not have. Comparing complete ordered metadata
    /// prevents a CRUD test from passing under an access path or uniqueness rule production never receives.
    /// </remarks>
    [Fact]
    public async Task ProvisionedSchema_IndexesMatchTheMappedTerminalInventory()
    {
        IReadOnlyList<IndexMetadata> expected = ReadModelIndexes();
        IReadOnlyList<IndexMetadata> actual = await ReadDatabaseIndexesAsync();

        expected.Should().HaveCount(33);
        actual.Should().Equal(
            expected,
            "the embedded integration schema must reproduce every mapped terminal index exactly");
    }

    /// <summary>
    /// Every physical foreign key has the terminal name, ordered columns, principal and delete action.
    /// </summary>
    /// <returns>A task representing the assertion.</returns>
    /// <remarks>
    /// The conceptual relationships named by <see cref="ConceptualOnlyForeignKeys"/> are intentionally absent
    /// from the physical inventory. In particular, neither <c>Permission.ModuleDefID</c> nor
    /// <c>ProfilePropertyDefinition.ModuleDefID</c> is constrained in the terminal legacy schema. Creating
    /// either key in tests lets integration CRUD pass under a constraint production does not have.
    /// </remarks>
    [Fact]
    public async Task ProvisionedSchema_ForeignKeysMatchTheTerminalPhysicalInventory()
    {
        IReadOnlyList<ForeignKeyMetadata> expected = ReadModelForeignKeys()
            .Where(foreignKey => !ConceptualOnlyForeignKeys.Contains(foreignKey.Name))
            .ToList();
        IReadOnlyList<ForeignKeyMetadata> actual = await ReadDatabaseForeignKeysAsync();

        expected.Should().HaveCount(29);
        actual.Should().Equal(
            expected,
            "the test database must neither invent a foreign key nor omit one the terminal schema enforces");
        actual.Should().NotContain(
            foreignKey => foreignKey.Name == "FK_Permission_ModuleDefinitions_ModuleDefID"
                || foreignKey.Name == "FK_ProfilePropertyDefinition_ModuleDefinitions_ModuleDefID");
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

        try
        {
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
        finally
        {
            // A SAFETY NET, not the assertion. The removal above is part of what this fact asserts, so it
            // stays in the body where its outcome is checked; this only covers the case where an assertion
            // failed BEFORE the removal ran and would otherwise have left an extra tenant in the shared
            // database for every later fact that counts portals. It is idempotent, so on the ordinary path it
            // affects nothing, and it asserts nothing, so it can never replace the failure that brought it
            // here.
            await EnsurePortalRemovedAsync(portalId);
        }
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

            await roles.AddAsync(role);
            await unitOfWork.SaveChangesAsync();

            roleId = role.RoleId;
        }

        try
        {
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

                Role? reread = await roles.GetByIdAsync(roleId, _fixture.Seed.PortalId);

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

        }
        finally
        {
            // The role is a row in the SHARED seeded tenant, so a failing assertion above must not leave it
            // behind: later facts enumerate that tenant's roles and count them, and an orphan turns one real
            // failure here into several unrelated ones elsewhere. The removal asserts nothing, so it cannot
            // displace the failure that brought it here, and it is a no-op when the role is already gone.
            await RemoveRoleAsync(roleId);
        }
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

            await roles.AddAsync(role);
            await unitOfWork.SaveChangesAsync();

            roleId = role.RoleId;
        }

        try
        {
            int nulls = await _fixture.Database.ScalarAsync<int>(
                "SELECT CASE WHEN [BillingFrequency] IS NULL AND [TrialFrequency] IS NULL THEN 1 ELSE 0 END "
                + "FROM [dbo].[Roles] WHERE [RoleID] = @roleId",
                new Dictionary<string, object?> { ["roleId"] = roleId });

            nulls.Should().Be(1);

            using (IServiceScope reading = _fixture.Services.CreateScope())
            {
                IRoleRepository roles = reading.ServiceProvider.GetRequiredService<IRoleRepository>();

                Role? reread = await roles.GetByIdAsync(roleId, _fixture.Seed.PortalId);

                reread.Should().NotBeNull();
                reread!.BillingFrequency.Should().BeNull();
                reread.TrialFrequency.Should().BeNull();
            }

        }
        finally
        {
            // The role is a row in the SHARED seeded tenant, so a failing assertion above must not leave it
            // behind: later facts enumerate that tenant's roles and count them, and an orphan turns one real
            // failure here into several unrelated ones elsewhere. The removal asserts nothing, so it cannot
            // displace the failure that brought it here, and it is a no-op when the role is already gone.
            await RemoveRoleAsync(roleId);
        }
    }

    /// <summary>
    /// A stored frequency character the enumeration does not declare reads back as the documented fallback,
    /// never as an undefined enumeration value.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <param name="stored">The character to plant in both frequency columns.</param>
    /// <remarks>
    /// <para>
    /// The rows this describes are not hypothetical. The shipped upgrade scripts seed the frequency
    /// vocabulary table with codes outside the six the application understands - <c>'4'</c> at
    /// <c>01.00.00.SqlDataProvider</c> L7192 and <c>'0'</c> at L7194 - and the one constraint that ever
    /// policed the columns, <c>FK_Roles_CodeFrequency</c>, is dropped for good at
    /// <c>03.00.01.SqlDataProvider</c> L1297 with no recreate and no check constraint in its place. The
    /// trial column was never covered by it at all. An installation in the field can therefore hold any
    /// character here.
    /// </para>
    /// <para>
    /// The consequence of casting such a character blindly is worse than a wrong value, which is why this
    /// is asserted through the real model rather than over the conversion in isolation. An undefined
    /// enumeration value materialises without complaint and travels intact to the wire converter, which
    /// refuses to write a code it does not recognise - so one out-of-vocabulary character in one row turns
    /// a successful read of an entire result set into a server fault. Resolving to the fallback degrades
    /// one field instead, and that is what the legacy application did in effect: it compared the raw
    /// character against the codes it knew and treated anything else as no recurrence.
    /// </para>
    /// <para>
    /// The lower-case case is included deliberately. SQL Server's default collation is case-insensitive, so
    /// a legacy installation could hold <c>'m'</c> and the dropped constraint would have accepted it - yet
    /// the legacy application compared with binary semantics and never read it as a month. Resolving it
    /// here would change behaviour rather than preserve it.
    /// </para>
    /// <para>
    /// BOTH columns are planted and both are asserted, and that pairing is the point rather than
    /// thoroughness for its own sake. The two columns are the same store type over the same vocabulary, so a
    /// guard added to one and not the other compiles, passes any single-column test, and fails only in the
    /// field - on the trial column first, since that is the one no constraint ever policed. Asserting the
    /// pair is what makes them unable to drift apart.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("4")]
    [InlineData("0")]
    [InlineData("m")]
    [InlineData("Z")]
    public async Task Role_WithAnUnrecognisedStoredFrequency_ReadsAsTheFallback(string stored)
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
                RoleName = FormattableString.Invariant($"Legacy Code Role {suffix}"),
                BillingFrequency = Domain.Enums.BillingFrequency.Month,
                TrialFrequency = Domain.Enums.BillingFrequency.Week,
                IsPublic = false,
                AutoAssignment = false,
            };

            await roles.AddAsync(role);
            await unitOfWork.SaveChangesAsync();

            roleId = role.RoleId;
        }

        try
        {
            // Planted with a direct statement rather than through the model, because the model cannot express
            // it - which is the point. Only a legacy row can carry this, so only a legacy row can prove the
            // read handles it.
            await _fixture.Database.ExecuteAsync(
                "UPDATE [dbo].[Roles] SET [BillingFrequency] = @stored, [TrialFrequency] = @stored "
                + "WHERE [RoleID] = @roleId",
                new Dictionary<string, object?> { ["stored"] = stored, ["roleId"] = roleId });

            using (IServiceScope reading = _fixture.Services.CreateScope())
            {
                IRoleRepository roles = reading.ServiceProvider.GetRequiredService<IRoleRepository>();

                Role? reread = await roles.GetByIdAsync(roleId, _fixture.Seed.PortalId);

                reread.Should().NotBeNull();

                reread!.BillingFrequency.Should().Be(Domain.Enums.BillingFrequency.None);
                reread.TrialFrequency.Should().Be(Domain.Enums.BillingFrequency.None);

                Enum.IsDefined(reread.BillingFrequency!.Value).Should().BeTrue(
                    "an undefined enumeration value reaches the wire and fails there, taking the whole "
                    + "response with it, so it must not be materialised in the first place");
                Enum.IsDefined(reread.TrialFrequency!.Value).Should().BeTrue();
            }

        }
        finally
        {
            // The role is a row in the SHARED seeded tenant, so a failing assertion above must not leave it
            // behind: later facts enumerate that tenant's roles and count them, and an orphan turns one real
            // failure here into several unrelated ones elsewhere. The removal asserts nothing, so it cannot
            // displace the failure that brought it here, and it is a no-op when the role is already gone.
            await RemoveRoleAsync(roleId);
        }
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

    /// <summary>The model maps exactly the twenty-one entities this migration owns, and no others.</summary>
    /// <remarks>
    /// The count is a genuine cross-file invariant: the context declares twenty-one sets, the configuration
    /// folder holds twenty-one configurations, and the domain declares twenty-one entities. Asserting
    /// equivalence rather than only the count is what makes the failure diagnostic - it names the entity
    /// that appeared or vanished instead of reporting that a number moved.
    /// </remarks>
    [Fact]
    public void Model_MapsExactlyTheTwentyOneOwnedEntities()
    {
        IReadOnlyList<Type> mapped = Model.GetEntityTypes()
            .Select(entity => entity.ClrType)
            .ToList();

        mapped.Should().BeEquivalentTo(
            MappedEntityTables.Select(row => row.Entity),
            "the context declares twenty-one sets and the configuration folder holds twenty-one "
            + "configurations, so the composed model must agree with both");

        mapped.Should().HaveCount(21);
    }

    /// <summary>Each entity binds to its legacy table under the legacy schema owner.</summary>
    /// <param name="entity">The entity type.</param>
    /// <param name="table">The legacy table it must bind to.</param>
    [Theory]
    [MemberData(nameof(MappedEntities))]
    public void Model_BindsTheEntityToItsLegacyTable(Type entity, string table)
    {
        IEntityType mapped = MappedTypeOf(entity);

        mapped.GetTableName().Should().Be(table);
        mapped.GetSchema().Should().Be(
            LegacySchema,
            "the legacy installation owns its objects as dbo with an empty object qualifier");
    }

    /// <summary>Every mapped entity resolves a non-empty table name and a primary key.</summary>
    /// <remarks>
    /// A blanket assertion rather than a per-entity one, so an entity added later without a table binding or
    /// without a key is caught even though nobody remembered to extend the mapping table above. An entity
    /// with no explicit table name would silently fall back to the set name, which for six of these tables
    /// is the wrong, pluralised spelling.
    /// </remarks>
    [Fact]
    public void Model_GivesEveryEntityATableNameAndAPrimaryKey()
    {
        foreach (IEntityType entity in Model.GetEntityTypes())
        {
            entity.GetTableName().Should().NotBeNullOrEmpty(
                FormattableString.Invariant($"{entity.ClrType.Name} must bind to a named table"));

            entity.GetSchema().Should().Be(
                LegacySchema,
                FormattableString.Invariant($"{entity.ClrType.Name} must be owned by dbo"));

            entity.FindPrimaryKey().Should().NotBeNull(
                FormattableString.Invariant($"{entity.ClrType.Name} must declare a primary key"));
        }
    }

    /// <summary>The six table names that stayed singular are not pluralised by the mapping.</summary>
    /// <remarks>
    /// Called out separately from the mapping table because this is the single highest-value assertion in the
    /// file. The set is plural in every case, so a reader correcting the "inconsistency" would break every
    /// installation in the field, and <c>UserProfileValue</c> does not share a stem with its table at all.
    /// Each name was confirmed against the legacy scripts, two of them only under the
    /// <c>{databaseOwner}{objectQualifier}</c>-templated naming form - which is precisely why a
    /// single-form search over that chain reports objects as absent when they are present.
    /// </remarks>
    [Fact]
    public void Model_KeepsTheLegacyTableNamesThatStayedSingular()
    {
        foreach ((Type entity, string table) in SingularLegacyTables)
        {
            MappedTypeOf(entity).GetTableName().Should().Be(
                table,
                FormattableString.Invariant(
                    $"{entity.Name} binds to the singular {table}, not to a pluralised spelling"));
        }
    }

    /// <summary>Each property binds to the exact legacy column spelling.</summary>
    /// <param name="entity">The entity that declares the property.</param>
    /// <param name="property">The property name.</param>
    /// <param name="column">The legacy column it must bind to.</param>
    [Theory]
    [MemberData(nameof(LegacyColumnSpellings))]
    public void Model_PinsTheLegacyColumnSpelling(Type entity, string property, string column)
    {
        IProperty? bound = MappedTypeOf(entity).FindProperty(property);

        bound.Should().NotBeNull(
            FormattableString.Invariant($"{entity.Name}.{property} must be a mapped property"));

        string qualified = FormattableString.Invariant($"{entity.Name}.{property}");

        bound!.GetColumnName().Should().Be(
            column,
            qualified + " carries its legacy column spelling, and correcting that spelling would read a "
            + "genuine installation as empty");
    }

    /// <summary>The three key-value and join tables declare composite primary keys, in column order.</summary>
    /// <remarks>
    /// <para>
    /// Order is asserted, not just membership, because a composite key's order determines the clustered
    /// index's leading column and therefore which lookups are cheap.
    /// </para>
    /// <para>
    /// The two settings tables differ in their first member - <c>ModuleSettings</c> keys on the module while
    /// <c>TabModuleSettings</c> keys on the placement - which is exactly the pair a copy-paste would
    /// conflate. Both are genuine key-value tables: the legacy chain alters <c>ModuleSettings</c> thirteen
    /// times and <c>TabModuleSettings</c> eight.
    /// </para>
    /// </remarks>
    [Fact]
    public void Model_DeclaresTheCompositePrimaryKeys()
    {
        PrimaryKeyOf(typeof(ModuleSetting)).Should().Equal(
            nameof(ModuleSetting.ModuleId),
            nameof(ModuleSetting.SettingName));

        PrimaryKeyOf(typeof(TabModuleSetting)).Should().Equal(
            nameof(TabModuleSetting.TabModuleId),
            nameof(TabModuleSetting.SettingName));

        PrimaryKeyOf(typeof(UserPortal)).Should().Equal(
            nameof(UserPortal.UserId),
            nameof(UserPortal.PortalId));
    }

    /// <summary>
    /// The portal-membership join keys on the user and portal pair even though it also carries a surrogate
    /// identifier.
    /// </summary>
    /// <remarks>
    /// This is the trap the composite key above exists to avoid. <c>UserPortal</c> declares an integer
    /// <c>UserPortalId</c>, so Entity Framework's key convention would have chosen it, silently and without
    /// any build or model-validation complaint - and the resulting model would permit two membership rows for
    /// the same account in the same tenant. Asserting that the surrogate is mapped but is NOT the key pins
    /// both halves of that distinction.
    /// </remarks>
    [Fact]
    public void Model_TreatsTheMembershipSurrogateAsAColumnRatherThanTheKey()
    {
        IEntityType membership = MappedTypeOf(typeof(UserPortal));

        membership.FindProperty(nameof(UserPortal.UserPortalId)).Should().NotBeNull(
            "the surrogate is still a mapped column");

        PrimaryKeyOf(typeof(UserPortal)).Should().NotContain(
            nameof(UserPortal.UserPortalId),
            "the by-convention key would have chosen the surrogate and allowed duplicate memberships");
    }

    /// <summary>Every primary-key constraint is named after the table it constrains.</summary>
    /// <remarks>
    /// The legacy constraint names are templated with the object qualifier, which is empty for this
    /// installation, so they collapse to <c>PK_</c> followed by the unqualified table name. Asserting the
    /// name against the TABLE rather than against a literal list is what makes this hold for the six
    /// singular tables too, and what keeps the assertion honest if a table name ever changes.
    /// </remarks>
    [Theory]
    [MemberData(nameof(MappedEntities))]
    public void Model_NamesThePrimaryKeyAfterItsTable(Type entity, string table)
    {
        IKey? key = MappedTypeOf(entity).FindPrimaryKey();

        key.Should().NotBeNull();
        key!.GetName().Should().Be(FormattableString.Invariant($"PK_{table}"));
    }

    /// <summary>
    /// The externally stored account properties are absent from the model while remaining present on the
    /// type.
    /// </summary>
    /// <param name="property">The property that must not be mapped.</param>
    /// <remarks>
    /// Both halves matter. That the property is unmapped is the mapping contract; that the CLR property still
    /// exists is what distinguishes a deliberate exclusion from a misspelling, since <c>FindProperty</c>
    /// answers null for either. No catalogue query can make that distinction, which is why this assertion can
    /// only live in the model layer.
    /// </remarks>
    [Theory]
    [MemberData(nameof(ExternallyStoredAccountProperties))]
    public void Model_LeavesTheExternallyStoredAccountPropertyUnmapped(string property)
    {
        IEntityType account = MappedTypeOf(typeof(User));
        string qualified = FormattableString.Invariant($"User.{property}");

        account.FindProperty(property).Should().BeNull(
            qualified + " is held in the external membership store and composed by the repository, so "
            + "mapping it as a column on dbo.Users would fail against a real installation");

        typeof(User).GetProperty(property).Should().NotBeNull(
            qualified + " must still exist on the type - if it does not, this assertion is passing on a "
            + "misspelling rather than on a deliberate exclusion");
    }

    /// <summary>The module capability flags are computed rather than mapped.</summary>
    /// <remarks>
    /// MIGRATION: these three restate the legacy <c>IPortable</c>, <c>ISearchable</c> and
    /// <c>IUpgradeable</c> contracts, which the legacy code discovered by late-bound activation of the
    /// module's business controller. They are derived from the stored controller class rather than stored
    /// themselves, so they are read-only computed properties; leaving them mapped would have Entity Framework
    /// look for three columns the schema has never had.
    /// </remarks>
    [Fact]
    public void Model_LeavesTheModuleCapabilityFlagsUnmapped()
    {
        IEntityType desktopModule = MappedTypeOf(typeof(DesktopModule));

        string[] computed =
        [
            nameof(DesktopModule.IsPortable),
            nameof(DesktopModule.IsSearchable),
            nameof(DesktopModule.IsUpgradeable),
        ];

        foreach (string flag in computed)
        {
            desktopModule.FindProperty(flag).Should().BeNull(
                FormattableString.Invariant(
                    $"DesktopModule.{flag} is computed from the business controller class, not stored"));

            typeof(DesktopModule).GetProperty(flag).Should().NotBeNull(
                FormattableString.Invariant($"DesktopModule.{flag} must still exist on the type"));
        }
    }

    /// <summary>
    /// The model declares no entity for the legacy per-request settings composite, because no such table
    /// exists.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A plausible-sounding aggregate that must be shown ABSENT, which is why it is asserted by name: the
    /// type does not exist, so referencing it would not compile, and no catalogue query can demonstrate the
    /// absence of a table that was never going to be there.
    /// </para>
    /// <para>
    /// Four independent findings establish it. The legacy abstract data provider declares no such member
    /// among its two hundred and sixty-nine; the core SQL provider invokes no such procedure among its two
    /// hundred and forty-five; searching all eighty-eight upgrade scripts case-insensitively finds no such
    /// table, only <c>ModuleSettings</c>, <c>HostSettings</c>, <c>TabModuleSettings</c>,
    /// <c>ScheduleItemSettings</c> and a transient <c>Tmp_ModuleSettings</c>; and positively,
    /// <c>PortalController.vb:L1209-L1210</c> reads the legacy composite out of the request items
    /// collection. It was an ambient per-request object, not a persisted aggregate - portal configuration
    /// lives as columns on <c>Portals</c>, and the legacy class maps to the scoped tenant context.
    /// </para>
    /// </remarks>
    [Fact]
    public void Model_DeclaresNoAggregateForTheAmbientTenantSettingsComposite()
    {
        IReadOnlyList<string> mappedTypes = Model.GetEntityTypes()
            .Select(entity => entity.ClrType.Name)
            .ToList();

        IReadOnlyList<string?> mappedTables = Model.GetEntityTypes()
            .Select(entity => entity.GetTableName())
            .ToList();

        mappedTypes.Should().NotContain("PortalSetting");
        mappedTables.Should().NotContain("PortalSettings");
    }

    /// <summary>
    /// The model declares no entity for the excluded file-management tables or the external membership
    /// tables.
    /// </summary>
    /// <remarks>
    /// The folder and folder-permission tables genuinely exist in the legacy schema, and the
    /// <c>aspnet_</c>-prefixed tables genuinely exist in a real installation. Neither belongs to this model:
    /// file management is out of scope, and the membership store is installed externally. Asserting their
    /// absence keeps a later "the table is right there" addition from silently widening the mapped surface.
    /// </remarks>
    [Fact]
    public void Model_DeclaresNoAggregateForExcludedOrExternallyOwnedTables()
    {
        IReadOnlyList<string> mappedTypes = Model.GetEntityTypes()
            .Select(entity => entity.ClrType.Name)
            .ToList();

        IReadOnlyList<string> mappedTables = Model.GetEntityTypes()
            .Select(entity => entity.GetTableName() ?? string.Empty)
            .ToList();

        mappedTypes.Should().NotContain("Folder");
        mappedTypes.Should().NotContain("FolderPermission");
        mappedTables.Should().NotContain("Folders");
        mappedTables.Should().NotContain("FolderPermission");

        mappedTypes.Should().NotContain(
            name => name.StartsWith("aspnet_", StringComparison.OrdinalIgnoreCase),
            "the membership store is installed externally and is only ever altered by the legacy chain");

        mappedTables.Should().NotContain(
            name => name.StartsWith("aspnet_", StringComparison.OrdinalIgnoreCase),
            "the membership store is installed externally and is only ever altered by the legacy chain");
    }

    /// <summary>Projects every non-primary model index into its physical database shape.</summary>
    /// <returns>The complete ordered index inventory.</returns>
    private static IReadOnlyList<IndexMetadata> ReadModelIndexes()
    {
        var indexes = new List<IndexMetadata>();

        foreach (IEntityType entity in Model.GetEntityTypes())
        {
            string tableName = entity.GetTableName()
                ?? throw new InvalidOperationException(
                    FormattableString.Invariant($"{entity.ClrType.Name} has no table name."));
            StoreObjectIdentifier table = StoreObjectIdentifier.Table(tableName, entity.GetSchema());

            foreach (IIndex index in entity.GetIndexes())
            {
                string name = index.GetDatabaseName()
                    ?? throw new InvalidOperationException(
                        FormattableString.Invariant($"{entity.ClrType.Name} has an unnamed index."));
                string columns = string.Join(
                    ",",
                    index.Properties.Select(property => property.GetColumnName(table)
                        ?? throw new InvalidOperationException(
                            FormattableString.Invariant(
                                $"{entity.ClrType.Name}.{property.Name} has no column name."))));

                indexes.Add(new IndexMetadata(
                    tableName,
                    name,
                    columns,
                    index.IsUnique,
                    index.GetFilter()));
            }
        }

        return indexes
            .OrderBy(index => index.Table, StringComparer.Ordinal)
            .ThenBy(index => index.Name, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Reads every non-primary index from the provisioned SQL Server catalogue.</summary>
    /// <returns>The complete ordered index inventory.</returns>
    private async Task<IReadOnlyList<IndexMetadata>> ReadDatabaseIndexesAsync()
    {
        const string query =
            """
            SELECT
                t.[name] AS [TableName],
                i.[name] AS [IndexName],
                STRING_AGG(c.[name], N',') WITHIN GROUP (ORDER BY ic.[key_ordinal]) AS [Columns],
                CAST(i.[is_unique] AS bit) AS [IsUnique],
                i.[filter_definition] AS [FilterDefinition]
            FROM sys.indexes AS i
            INNER JOIN sys.tables AS t ON t.[object_id] = i.[object_id]
            INNER JOIN sys.schemas AS s ON s.[schema_id] = t.[schema_id]
            INNER JOIN sys.index_columns AS ic
                ON ic.[object_id] = i.[object_id]
                AND ic.[index_id] = i.[index_id]
                AND ic.[key_ordinal] > 0
                AND ic.[is_included_column] = 0
            INNER JOIN sys.columns AS c
                ON c.[object_id] = ic.[object_id]
                AND c.[column_id] = ic.[column_id]
            WHERE s.[name] = N'dbo'
                AND i.[index_id] > 0
                AND i.[is_primary_key] = 0
                AND i.[is_hypothetical] = 0
            GROUP BY t.[name], i.[name], i.[is_unique], i.[filter_definition]
            ORDER BY t.[name], i.[name];
            """;

        var indexes = new List<IndexMetadata>();
        var mappedTables = MappedTables.ToHashSet(StringComparer.Ordinal);

        await using var connection = new SqlConnection(_fixture.Database.ConnectionString);
        await connection.OpenAsync();

        await using var command = new SqlCommand(query, connection);
        await using SqlDataReader reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            string table = reader.GetString(0);
            if (!mappedTables.Contains(table))
            {
                continue;
            }

            indexes.Add(new IndexMetadata(
                table,
                reader.GetString(1),
                reader.GetString(2),
                reader.GetBoolean(3),
                reader.IsDBNull(4) ? null : reader.GetString(4)));
        }

        return indexes
            .OrderBy(index => index.Table, StringComparer.Ordinal)
            .ThenBy(index => index.Name, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Projects every model relationship into its physical foreign-key shape.</summary>
    /// <returns>The complete ordered relationship inventory, including conceptual-only relationships.</returns>
    private static IReadOnlyList<ForeignKeyMetadata> ReadModelForeignKeys()
    {
        var foreignKeys = new List<ForeignKeyMetadata>();

        foreach (IEntityType entity in Model.GetEntityTypes())
        {
            string tableName = entity.GetTableName()
                ?? throw new InvalidOperationException(
                    FormattableString.Invariant($"{entity.ClrType.Name} has no table name."));
            StoreObjectIdentifier table = StoreObjectIdentifier.Table(tableName, entity.GetSchema());

            foreach (IForeignKey foreignKey in entity.GetForeignKeys())
            {
                IEntityType principal = foreignKey.PrincipalEntityType;
                string principalTableName = principal.GetTableName()
                    ?? throw new InvalidOperationException(
                        FormattableString.Invariant($"{principal.ClrType.Name} has no table name."));
                StoreObjectIdentifier principalTable =
                    StoreObjectIdentifier.Table(principalTableName, principal.GetSchema());
                string name = foreignKey.GetConstraintName()
                    ?? throw new InvalidOperationException(
                        FormattableString.Invariant(
                            $"{entity.ClrType.Name} to {principal.ClrType.Name} has no constraint name."));
                string columns = string.Join(
                    ",",
                    foreignKey.Properties.Select(property => property.GetColumnName(table)
                        ?? throw new InvalidOperationException(
                            FormattableString.Invariant(
                                $"{entity.ClrType.Name}.{property.Name} has no column name."))));
                string principalColumns = string.Join(
                    ",",
                    foreignKey.PrincipalKey.Properties.Select(property => property.GetColumnName(principalTable)
                        ?? throw new InvalidOperationException(
                            FormattableString.Invariant(
                                $"{principal.ClrType.Name}.{property.Name} has no column name."))));

                foreignKeys.Add(new ForeignKeyMetadata(
                    tableName,
                    name,
                    columns,
                    principalTableName,
                    principalColumns,
                    ToReferentialAction(foreignKey.DeleteBehavior)));
            }
        }

        return foreignKeys
            .OrderBy(foreignKey => foreignKey.Table, StringComparer.Ordinal)
            .ThenBy(foreignKey => foreignKey.Name, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Reads every foreign key from the provisioned SQL Server catalogue.</summary>
    /// <returns>The complete ordered physical foreign-key inventory.</returns>
    private async Task<IReadOnlyList<ForeignKeyMetadata>> ReadDatabaseForeignKeysAsync()
    {
        const string query =
            """
            SELECT
                dependentTable.[name] AS [TableName],
                foreignKey.[name] AS [ForeignKeyName],
                STRING_AGG(dependentColumn.[name], N',')
                    WITHIN GROUP (ORDER BY foreignKeyColumn.[constraint_column_id]) AS [Columns],
                principalTable.[name] AS [PrincipalTableName],
                STRING_AGG(principalColumn.[name], N',')
                    WITHIN GROUP (ORDER BY foreignKeyColumn.[constraint_column_id]) AS [PrincipalColumns],
                foreignKey.[delete_referential_action_desc] AS [DeleteAction]
            FROM sys.foreign_keys AS foreignKey
            INNER JOIN sys.foreign_key_columns AS foreignKeyColumn
                ON foreignKeyColumn.[constraint_object_id] = foreignKey.[object_id]
            INNER JOIN sys.tables AS dependentTable
                ON dependentTable.[object_id] = foreignKey.[parent_object_id]
            INNER JOIN sys.schemas AS dependentSchema
                ON dependentSchema.[schema_id] = dependentTable.[schema_id]
            INNER JOIN sys.columns AS dependentColumn
                ON dependentColumn.[object_id] = foreignKeyColumn.[parent_object_id]
                AND dependentColumn.[column_id] = foreignKeyColumn.[parent_column_id]
            INNER JOIN sys.tables AS principalTable
                ON principalTable.[object_id] = foreignKey.[referenced_object_id]
            INNER JOIN sys.columns AS principalColumn
                ON principalColumn.[object_id] = foreignKeyColumn.[referenced_object_id]
                AND principalColumn.[column_id] = foreignKeyColumn.[referenced_column_id]
            WHERE dependentSchema.[name] = N'dbo'
            GROUP BY
                dependentTable.[name],
                foreignKey.[name],
                principalTable.[name],
                foreignKey.[delete_referential_action_desc]
            ORDER BY dependentTable.[name], foreignKey.[name];
            """;

        var foreignKeys = new List<ForeignKeyMetadata>();
        var mappedTables = MappedTables.ToHashSet(StringComparer.Ordinal);

        await using var connection = new SqlConnection(_fixture.Database.ConnectionString);
        await connection.OpenAsync();

        await using var command = new SqlCommand(query, connection);
        await using SqlDataReader reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            string table = reader.GetString(0);
            if (!mappedTables.Contains(table))
            {
                continue;
            }

            foreignKeys.Add(new ForeignKeyMetadata(
                table,
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5)));
        }

        return foreignKeys
            .OrderBy(foreignKey => foreignKey.Table, StringComparer.Ordinal)
            .ThenBy(foreignKey => foreignKey.Name, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Converts an EF delete behaviour to SQL Server's catalogue vocabulary.</summary>
    /// <param name="deleteBehavior">The configured model behaviour.</param>
    /// <returns>The corresponding <c>sys.foreign_keys</c> action.</returns>
    private static string ToReferentialAction(DeleteBehavior deleteBehavior) =>
        deleteBehavior switch
        {
            DeleteBehavior.Cascade => "CASCADE",
            DeleteBehavior.SetNull => "SET_NULL",
            DeleteBehavior.NoAction
                or DeleteBehavior.Restrict
                or DeleteBehavior.ClientSetNull
                or DeleteBehavior.ClientCascade
                or DeleteBehavior.ClientNoAction => "NO_ACTION",
            _ => throw new ArgumentOutOfRangeException(
                nameof(deleteBehavior),
                deleteBehavior,
                "The mapped delete behaviour has no SQL Server catalogue equivalent."),
        };

    /// <summary>Composes the entity model from the production registration, without any database.</summary>
    /// <returns>The composed model.</returns>
    /// <remarks>
    /// <para>
    /// <strong>The internal context type is never named, and none of this needs it to be.</strong> The
    /// production registration is invoked exactly as the host invokes it, which registers the NON-generic
    /// <c>DbContextOptions</c> alongside its generic self. That non-generic type is public, and its
    /// <c>ContextType</c> property hands back the context's <see cref="Type"/> - which is all the service
    /// provider needs to resolve the instance, and which then casts to the public <see cref="DbContext"/>
    /// base whose metadata surface carries everything asserted above. No reflection over the infrastructure
    /// assembly, no <c>InternalsVisibleTo</c>, and no type name.
    /// </para>
    /// <para>
    /// <strong>Activating the context directly is deliberately avoided, and must stay avoided.</strong> The
    /// context declares a single constructor taking the GENERIC <c>DbContextOptions&lt;T&gt;</c>, so building
    /// a non-generic <c>DbContextOptionsBuilder</c> and activating with its <c>Options</c> throws
    /// <see cref="MissingMethodException"/> at run time: the non-generic options type is the base class, not
    /// the derived generic the constructor demands. Resolving from the container sidesteps that entirely,
    /// which is why this is not "simplified" into an activation call.
    /// </para>
    /// <para>
    /// <strong>No database is contacted.</strong> The model is built from the entity configurations, and a
    /// provider connects only when a query executes. Nothing here executes one, so this composes correctly
    /// even where no server exists - which is also why it must never gain an <c>EnsureCreated</c> to "make
    /// the model available".
    /// </para>
    /// <para>
    /// Scope validation is switched on and the context is resolved from a child scope rather than the root
    /// provider, matching how the host resolves it and how the request pipeline uses it.
    /// </para>
    /// <para>
    /// <strong><c>ValidateOnBuild</c> is deliberately NOT enabled here, and enabling it would break this
    /// method rather than strengthen it.</strong> That was established by trying it: it fails while
    /// constructing call sites for <c>ILogger&lt;T&gt;</c> and <c>IConfiguration</c>, which this bare
    /// collection never registers because it registers the infrastructure layer and nothing else. Those
    /// services are contributed by the host builder, not by <c>AddInfrastructure</c>, so their absence is a
    /// property of this deliberately minimal collection and NOT a defect in the registration under test.
    /// The guarantee itself is not lost: the shared fixture builds the REAL host with both
    /// <c>ValidateScopes</c> and <c>ValidateOnBuild</c> on, so captive dependencies and missing
    /// registrations are already caught there, against the complete graph, where the check is meaningful.
    /// Adding host registrations here purely to satisfy the flag would couple model composition to services
    /// the model does not use, and would give up the property that makes this method cheap - that it needs
    /// no host and no database at all.
    /// </para>
    /// </remarks>
    private static IModel ComposeModel()
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["ConnectionStrings:Default"] = ModelOnlyConnectionString,
            })
            .Build();

        ServiceCollection services = new();
        services.AddInfrastructure(configuration);

        using ServiceProvider provider = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateScopes = true });

        using IServiceScope scope = provider.CreateScope();

        DbContextOptions options = scope.ServiceProvider.GetRequiredService<DbContextOptions>();
        DbContext context = (DbContext)scope.ServiceProvider.GetRequiredService(options.ContextType);

        return context.Model;
    }

    /// <summary>Resolves an entity's mapping metadata, failing with a named diagnosis when it is absent.</summary>
    /// <param name="entity">The entity type.</param>
    /// <returns>The mapping metadata.</returns>
    /// <exception cref="InvalidOperationException">The entity is not part of the composed model.</exception>
    /// <remarks>
    /// Raising here rather than returning a nullable keeps every caller free of null handling, and names the
    /// entity in the message - a bare null-reference failure inside a theory would report only that something
    /// was null, for one of twenty-one cases.
    /// </remarks>
    private static IEntityType MappedTypeOf(Type entity) =>
        Model.FindEntityType(entity)
            ?? throw new InvalidOperationException(
                FormattableString.Invariant($"{entity.Name} is not part of the composed model."));

    /// <summary>Names the primary-key properties of an entity, in key order.</summary>
    /// <param name="entity">The entity type.</param>
    /// <returns>The key property names, in order.</returns>
    /// <exception cref="InvalidOperationException">The entity declares no primary key.</exception>
    private static IReadOnlyList<string> PrimaryKeyOf(Type entity)
    {
        IKey key = MappedTypeOf(entity).FindPrimaryKey()
            ?? throw new InvalidOperationException(
                FormattableString.Invariant($"{entity.Name} declares no primary key."));

        return key.Properties.Select(property => property.Name).ToList();
    }

    private sealed record IndexMetadata(
        string Table,
        string Name,
        string Columns,
        bool IsUnique,
        string? Filter);

    private sealed record ForeignKeyMetadata(
        string Table,
        string Name,
        string Columns,
        string PrincipalTable,
        string PrincipalColumns,
        string DeleteAction);
    /// <summary>Ensures a portal created by this suite is gone, whatever else happened.</summary>
    /// <param name="portalId">The portal to remove.</param>
    /// <returns>A task that completes when no such row remains.</returns>
    /// <remarks>
    /// A direct statement rather than the repository, and deliberately so: this runs on the failure path, where
    /// the model or the unit of work may be the very thing that is broken, and a cleanup that depends on the
    /// component under test cannot be relied upon to clean up. The row has no dependents - it was written bare
    /// by this suite - so one statement is sufficient, and the statement is unconditional so calling it twice
    /// costs nothing.
    /// </remarks>
    private Task EnsurePortalRemovedAsync(int portalId) => _fixture.Database.ExecuteAsync(
        "DELETE FROM [dbo].[Portals] WHERE [PortalID] = @portalId",
        new Dictionary<string, object?> { ["portalId"] = portalId });

    /// <summary>Removes a role created by this suite.</summary>
    /// <param name="roleId">The role to remove.</param>
    /// <returns>A task that completes when the role is gone.</returns>
    private async Task RemoveRoleAsync(int roleId)
    {
        using IServiceScope scope = _fixture.Services.CreateScope();
        IRoleRepository roles = scope.ServiceProvider.GetRequiredService<IRoleRepository>();
        IUnitOfWork unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        // DeleteAsync carries the key alone, exactly as the legacy DeleteRole did, and is a no-op when
        // no such role exists - so the read-then-remove pair this replaces is no longer needed.
        await roles.DeleteAsync(roleId);
        await unitOfWork.SaveChangesAsync();
    }

    /// <summary>Produces a short random suffix so concurrently executing suites cannot collide.</summary>
    /// <returns>A twelve-character suffix.</returns>
    private static string Suffix() => Guid.NewGuid().ToString("N")[..12];
}
