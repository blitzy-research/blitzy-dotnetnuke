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
/// Proves that the provisioned integration database reproduces the TERMINAL legacy column definitions the
/// entity model binds to, rather than an earlier state of the eighty-eight-script upgrade chain.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why this suite exists as its own file.</strong> Every other suite here asks whether the model and
/// the repositories behave correctly against the fixture. None of them asks whether the FIXTURE is right, and
/// that gap is not theoretical: three column declarations in <c>Schema/DnnSchema.sql</c> had drifted to
/// pre-terminal definitions - <c>PortalAlias.HTTPAlias</c> declared <c>NOT NULL</c>, and the two
/// permission/control key columns declared at their twenty-character baseline widths. The consequence is the
/// worst kind: the whole suite passed while REJECTING values a real installation accepts, so it could not
/// prove compatibility for legacy-valid data and it reported false confidence instead of a defect.
/// </para>
/// <para>
/// <strong>Two directions, because one is not enough.</strong> The first two assertions compare the composed
/// <see cref="IModel"/> against <c>INFORMATION_SCHEMA</c> for EVERY mapped column, which catches a drift in
/// either artefact - a fixture that narrows a column and a configuration that widens one are the same failure
/// from opposite ends. The remaining assertions then WRITE the values the terminal schema permits and the
/// baseline forbade, because a catalogue comparison alone cannot prove that a write actually lands.
/// </para>
/// <para>
/// MIGRATION: the terminal state is the only meaningful one. The upgrade chain is destructive and
/// append-only - objects are dropped, recreated and widened across eighty-eight scripts - so a declaration
/// copied from a baseline <c>CREATE TABLE</c> is wrong by construction even though it looks authoritative.
/// The three columns exercised below each carry their provenance:
/// </para>
/// <list type="bullet">
///   <item><description>
///     <c>PortalAlias.HTTPAlias</c> is created NULLABLE at <c>02.02.02.SqlDataProvider:3805-3808</c>
///     (<c>[HTTPAlias] [nvarchar] (200)</c>, no <c>NOT NULL</c>) and no later script alters it.
///   </description></item>
///   <item><description>
///     <c>ModuleControls.ControlKey</c> is widened to <c>nvarchar(50)</c> at
///     <c>02.02.00.SqlDataProvider:459-460</c>.
///   </description></item>
///   <item><description>
///     <c>Permission.PermissionKey</c> starts at <c>varchar(20)</c>
///     (<c>02.02.00.SqlDataProvider:688</c>) and is widened to <c>varchar(50) not null</c> at
///     <c>04.06.00.SqlDataProvider:393-398</c> under the heading "enlarge permission key field".
///   </description></item>
/// </list>
/// <para>
/// MIGRATION: no schema is created, altered or dropped from this file. <c>EnsureCreated</c>,
/// <c>EnsureDeleted</c> and <c>Database.Migrate</c> are absent and must stay absent, because the terminal
/// DotNetNuke schema depends on membership objects the upgrade scripts only ever ALTER.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
[Collection(IntegrationTestCollection.Name)]
public sealed class LegacySchemaFidelityTests
{
    /// <summary>
    /// A module-definition identifier no seeded or suite-created definition bears, used so that a row
    /// planted by direct statement cannot be reached by any repository read scoped to a real definition.
    /// </summary>
    private const int UnreachableModuleDefinitionId = 987654;

    /// <summary>A connection string used only to compose the model; nothing is opened with it.</summary>
    /// <remarks>
    /// The model is composed from configuration alone. Registering the infrastructure requires a connection
    /// string to be present, but building the model never opens a connection, so this value is deliberately
    /// unreachable rather than pointing at a real server.
    /// </remarks>
    private const string ModelOnlyConnectionString =
        "Server=(localdb)\\model-only;Database=DnnMigrationModelOnly;Integrated Security=true";

    /// <summary>The composed relational model, built once for the whole suite.</summary>
    /// <remarks>
    /// Composed through the same entirely public route the sibling context suite uses: the infrastructure
    /// registration exposes <see cref="DbContextOptions"/>, whose <see cref="DbContextOptions.ContextType"/>
    /// resolves the context as a <see cref="DbContext"/>. The internal context type is never named, so
    /// <c>InternalsVisibleTo</c> is not required and Rule T3 is honoured.
    /// </remarks>
    private static readonly IModel Model = ComposeModel();

    private readonly ApiTestFixture _fixture;

    /// <summary>Initialises a new instance of the <see cref="LegacySchemaFidelityTests"/> class.</summary>
    /// <param name="fixture">The shared host and provisioned database.</param>
    public LegacySchemaFidelityTests(ApiTestFixture fixture) => _fixture = fixture;

    /// <summary>
    /// Every mapped column is nullable in the provisioned database exactly where the model maps it nullable.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// A model that maps a column nullable while the database declares it <c>NOT NULL</c> fails only when a
    /// null is written, and a model that maps a column required while the database permits nulls fails only
    /// when an existing null is read. Both are invisible to a build, to a migration diff and to every test
    /// that happens to supply a value, which is precisely how the alias defect survived.
    /// </remarks>
    [Fact]
    public async Task EveryMappedColumn_IsNullableExactlyWhereTheModelSaysSo()
    {
        IReadOnlyDictionary<string, CatalogueColumn> catalogue = await ReadCatalogueColumnsAsync();
        var mismatches = new List<string>();

        foreach (MappedColumn column in EnumerateMappedColumns())
        {
            if (!catalogue.TryGetValue(column.Key, out CatalogueColumn? provisioned))
            {
                mismatches.Add(FormattableString.Invariant($"{column.Key}:absent from the database"));
                continue;
            }

            if (provisioned.IsNullable != column.IsNullable)
            {
                mismatches.Add(FormattableString.Invariant(
                    $"{column.Key}:model={column.IsNullable},db={provisioned.IsNullable}"));
            }
        }

        mismatches.Should().BeEmpty(
            "the fixture exists to reproduce the terminal legacy schema, so a nullability disagreement is "
            + "either a stale fixture declaration or a mis-mapped configuration and never an acceptable "
            + "difference");
    }

    /// <summary>
    /// Every mapped text column is as wide in the provisioned database as the model declares it.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// <para>
    /// A fixture narrower than the model rejects legacy-valid data with a truncation error that looks like a
    /// test-data mistake rather than a schema defect; a fixture wider than the model lets a test store a value
    /// a real installation would reject. Only the declared maximum lengths are compared, because a column with
    /// no configured length is one the provider sizes itself and there is nothing to disagree about.
    /// </para>
    /// <para>
    /// The catalogue reports a byte count for Unicode columns and a character count for the rest, so the
    /// comparison converts before asserting rather than comparing a length against a length that means
    /// something else.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task EveryMappedTextColumn_IsAsWideAsTheModelDeclares()
    {
        IReadOnlyDictionary<string, CatalogueColumn> catalogue = await ReadCatalogueColumnsAsync();
        var mismatches = new List<string>();

        foreach (MappedColumn column in EnumerateMappedColumns())
        {
            if (column.MaxLength is not int declaredLength)
            {
                continue;
            }

            if (!catalogue.TryGetValue(column.Key, out CatalogueColumn? provisioned)
                || provisioned.CharacterLength is not int provisionedLength)
            {
                continue;
            }

            if (provisionedLength != declaredLength)
            {
                mismatches.Add(FormattableString.Invariant(
                    $"{column.Key}:model={declaredLength},db={provisionedLength}"));
            }
        }

        mismatches.Should().BeEmpty(
            "a width the model and the database disagree about is a value one of them silently refuses");
    }

    /// <summary>
    /// A portal alias row with no host name is written and read back as a null rather than being refused.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// <para>
    /// The terminal column permits it, so the fixture must. The row is written through the repository rather
    /// than by statement, because the point is that the ordinary write path reaches a column the fixture
    /// previously refused, and it is read back both through the model and as a raw catalogue-level null so
    /// that a null and the empty string cannot be conflated - <c>Null.NullString</c> was the empty string in
    /// the legacy stack, which is exactly why the two must stay distinguishable here.
    /// </para>
    /// <para>
    /// <c>IX_PortalAlias</c> is UNIQUE over this one column and carries no filter, and SQL Server admits a
    /// single null into such an index. One null alias is therefore all this suite may hold at a time, which is
    /// why the row is removed on every exit path.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task PortalAlias_AcceptsTheNullHostNameTheTerminalSchemaPermits()
    {
        int portalAliasId = 0;

        try
        {
            using (IServiceScope writing = _fixture.Services.CreateScope())
            {
                IPortalAliasRepository aliases =
                    writing.ServiceProvider.GetRequiredService<IPortalAliasRepository>();
                IUnitOfWork unitOfWork = writing.ServiceProvider.GetRequiredService<IUnitOfWork>();

                PortalAlias alias = new()
                {
                    PortalId = _fixture.Seed.PortalId,
                    HttpAlias = null,
                };

                await aliases.AddAsync(alias);
                await unitOfWork.SaveChangesAsync();

                portalAliasId = alias.PortalAliasId;
            }

            portalAliasId.Should().NotBe(0, "the generated key is meaningful once the unit of work commits");

            int storedNullCount = await _fixture.Database.ScalarAsync<int>(
                "SELECT COUNT(*) FROM [dbo].[PortalAlias] "
                + "WHERE [PortalAliasID] = @portalAliasId AND [HTTPAlias] IS NULL",
                new Dictionary<string, object?> { ["portalAliasId"] = portalAliasId });

            storedNullCount.Should().Be(
                1,
                "the column holds a SQL null rather than the empty string the legacy sentinel used");

            using IServiceScope reading = _fixture.Services.CreateScope();
            IPortalAliasRepository reader =
                reading.ServiceProvider.GetRequiredService<IPortalAliasRepository>();

            PortalAlias? readBack = await reader.GetByIdAsync(portalAliasId);

            readBack.Should().NotBeNull();
            readBack!.HttpAlias.Should().BeNull("a stored null materialises as a null, not as an empty string");
            readBack.PortalId.Should().Be(_fixture.Seed.PortalId);
        }
        finally
        {
            if (portalAliasId != 0)
            {
                await _fixture.Database.ExecuteAsync(
                    "DELETE FROM [dbo].[PortalAlias] WHERE [PortalAliasID] = @portalAliasId",
                    new Dictionary<string, object?> { ["portalAliasId"] = portalAliasId });
            }
        }
    }

    /// <summary>
    /// A control key longer than the twenty-character baseline is written and read back unchanged.
    /// </summary>
    /// <param name="length">A key length the terminal column admits and the baseline did not.</param>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// Twenty-one characters is the first length the baseline declaration refused, and fifty is the terminal
    /// maximum, so the pair brackets the whole of the widening. The value is asserted character for character
    /// after the round trip, because a silent right-truncation would otherwise read as a pass.
    /// </remarks>
    [Theory]
    [Trait("Category", "Integration")]
    [InlineData(21)]
    [InlineData(50)]
    public async Task ModuleControl_AcceptsAControlKeyWiderThanTheBaselineDeclaration(int length)
    {
        string controlKey = KeyOfLength(length);
        int moduleControlId = 0;

        try
        {
            using (IServiceScope writing = _fixture.Services.CreateScope())
            {
                IModuleDefinitionRepository definitions =
                    writing.ServiceProvider.GetRequiredService<IModuleDefinitionRepository>();
                IUnitOfWork unitOfWork = writing.ServiceProvider.GetRequiredService<IUnitOfWork>();

                // ModuleDefID is nullable on this table, and a control that belongs to no definition is
                // exactly the shape that keeps this assertion about the key column and nothing else.
                ModuleControl control = new()
                {
                    ModuleDefinitionId = null,
                    ControlKey = controlKey,
                    ControlTitle = "Legacy width probe",
                    ControlSrc = FormattableString.Invariant($"DesktopModules/Probe/{controlKey}.ascx"),
                    ControlType = 0,
                    SupportsPartialRendering = false,
                };

                await definitions.AddModuleControlAsync(control);
                await unitOfWork.SaveChangesAsync();

                moduleControlId = control.ModuleControlId;
            }

            string stored = await _fixture.Database.ScalarAsync<string>(
                "SELECT [ControlKey] FROM [dbo].[ModuleControls] WHERE [ModuleControlID] = @moduleControlId",
                new Dictionary<string, object?> { ["moduleControlId"] = moduleControlId });

            stored.Should().Be(controlKey, "the stored value is neither truncated nor padded");
            stored.Length.Should().Be(length);

            using IServiceScope reading = _fixture.Services.CreateScope();
            IModuleDefinitionRepository reader =
                reading.ServiceProvider.GetRequiredService<IModuleDefinitionRepository>();

            ModuleControl? readBack = await reader.GetModuleControlByIdAsync(moduleControlId);

            readBack.Should().NotBeNull();
            readBack!.ControlKey.Should().Be(controlKey);
        }
        finally
        {
            if (moduleControlId != 0)
            {
                await _fixture.Database.ExecuteAsync(
                    "DELETE FROM [dbo].[ModuleControls] WHERE [ModuleControlID] = @moduleControlId",
                    new Dictionary<string, object?> { ["moduleControlId"] = moduleControlId });
            }
        }
    }

    /// <summary>
    /// A permission key longer than the twenty-character baseline is accepted by the provisioned column.
    /// </summary>
    /// <param name="length">A key length the terminal column admits and the baseline did not.</param>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// <para>
    /// The row is planted by direct statement rather than through the repository, and that is a property of
    /// the contract rather than a shortcut: <see cref="Permission.PermissionKey"/> is the closed
    /// <see cref="PermissionKey"/> enumeration whose widest member spells five characters, so no code path in
    /// the target can produce a longer value. What is under test here is the COLUMN, which a legacy
    /// installation may well have widened for a third-party module's permission vocabulary - the widening
    /// script exists precisely because one did.
    /// </para>
    /// <para>
    /// The row is addressed to a module-definition identifier nothing else reads, and removed on every exit
    /// path, so a key outside the enumeration can never reach a materialising query.
    /// </para>
    /// </remarks>
    [Theory]
    [Trait("Category", "Integration")]
    [InlineData(21)]
    [InlineData(50)]
    public async Task Permission_AcceptsAPermissionKeyWiderThanTheBaselineDeclaration(int length)
    {
        string permissionKey = KeyOfLength(length);
        string permissionCode = FormattableString.Invariant($"PROBE_{Suffix()}");

        try
        {
            int inserted = await _fixture.Database.ExecuteAsync(
                "INSERT INTO [dbo].[Permission] "
                + "([PermissionCode], [ModuleDefID], [PermissionKey], [PermissionName]) "
                + "VALUES (@permissionCode, @moduleDefId, @permissionKey, @permissionName)",
                new Dictionary<string, object?>
                {
                    ["permissionCode"] = permissionCode,
                    ["moduleDefId"] = UnreachableModuleDefinitionId,
                    ["permissionKey"] = permissionKey,
                    ["permissionName"] = "Legacy width probe",
                });

            inserted.Should().Be(
                1,
                "the terminal column is varchar(50), so a key of this length is a row a real installation "
                + "can already hold");

            string stored = await _fixture.Database.ScalarAsync<string>(
                "SELECT [PermissionKey] FROM [dbo].[Permission] WHERE [PermissionCode] = @permissionCode",
                new Dictionary<string, object?> { ["permissionCode"] = permissionCode });

            stored.Should().Be(permissionKey, "the stored value is neither truncated nor padded");
            stored.Length.Should().Be(length);
        }
        finally
        {
            await _fixture.Database.ExecuteAsync(
                "DELETE FROM [dbo].[Permission] WHERE [PermissionCode] = @permissionCode",
                new Dictionary<string, object?> { ["permissionCode"] = permissionCode });
        }
    }

    /// <summary>
    /// The three columns whose fixture declarations had drifted are provisioned at their terminal shapes.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// The two sweeping assertions above would already fail if any of these regressed, but they report a list
    /// rather than a cause. This states the three terminal facts individually so that a regression names the
    /// column, the expected shape and the script that established it.
    /// </remarks>
    [Fact]
    public async Task TheThreeDriftedColumns_AreProvisionedAtTheirTerminalShapes()
    {
        IReadOnlyDictionary<string, CatalogueColumn> catalogue = await ReadCatalogueColumnsAsync();

        CatalogueColumn alias = catalogue["PortalAlias.HTTPAlias"];
        alias.IsNullable.Should().BeTrue(
            "02.02.02.SqlDataProvider:3805-3808 creates the column with no NOT NULL clause and nothing "
            + "alters it afterwards");
        alias.DataType.Should().Be("nvarchar");
        alias.CharacterLength.Should().Be(200);

        CatalogueColumn controlKey = catalogue["ModuleControls.ControlKey"];
        controlKey.DataType.Should().Be("nvarchar");
        controlKey.CharacterLength.Should().Be(
            50,
            "02.02.00.SqlDataProvider:459-460 widens ControlKey from the baseline twenty characters");
        controlKey.IsNullable.Should().BeTrue();

        CatalogueColumn permissionKey = catalogue["Permission.PermissionKey"];
        permissionKey.DataType.Should().Be("varchar");
        permissionKey.CharacterLength.Should().Be(
            50,
            "04.06.00.SqlDataProvider:393-398 enlarges the permission key field to varchar(50) not null");
        permissionKey.IsNullable.Should().BeFalse();
    }

    /// <summary>Composes the relational model without naming the internal context type.</summary>
    /// <returns>The composed model.</returns>
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

    /// <summary>Projects every mapped column of the model into a comparable shape.</summary>
    /// <returns>One entry per column the model binds, keyed <c>Table.Column</c>.</returns>
    /// <remarks>
    /// A property the model deliberately does not map has no column name for its table and is skipped, which
    /// keeps an ignored property from being reported as an absent column.
    /// </remarks>
    private static IEnumerable<MappedColumn> EnumerateMappedColumns()
    {
        foreach (IEntityType entity in Model.GetEntityTypes())
        {
            string? tableName = entity.GetTableName();

            if (string.IsNullOrEmpty(tableName))
            {
                continue;
            }

            StoreObjectIdentifier table = StoreObjectIdentifier.Table(tableName, entity.GetSchema());

            foreach (IProperty property in entity.GetProperties())
            {
                string? columnName = property.GetColumnName(table);

                if (string.IsNullOrEmpty(columnName))
                {
                    continue;
                }

                yield return new MappedColumn(
                    FormattableString.Invariant($"{tableName}.{columnName}"),
                    property.IsColumnNullable(table),
                    property.GetMaxLength());
            }
        }
    }

    /// <summary>Reads every column of the provisioned <c>dbo</c> schema from the catalogue.</summary>
    /// <returns>One entry per provisioned column, keyed <c>Table.Column</c>.</returns>
    private async Task<IReadOnlyDictionary<string, CatalogueColumn>> ReadCatalogueColumnsAsync()
    {
        const string query =
            """
            SELECT
                [TABLE_NAME],
                [COLUMN_NAME],
                [DATA_TYPE],
                [IS_NULLABLE],
                [CHARACTER_MAXIMUM_LENGTH]
            FROM INFORMATION_SCHEMA.COLUMNS
            WHERE [TABLE_SCHEMA] = N'dbo'
            ORDER BY [TABLE_NAME], [COLUMN_NAME];
            """;

        var columns = new Dictionary<string, CatalogueColumn>(StringComparer.Ordinal);

        await using var connection = new SqlConnection(_fixture.Database.ConnectionString);
        await connection.OpenAsync();

        await using var command = new SqlCommand(query, connection);
        await using SqlDataReader reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            string table = reader.GetString(0);
            string column = reader.GetString(1);
            string dataType = reader.GetString(2);
            bool isNullable = string.Equals(reader.GetString(3), "YES", StringComparison.Ordinal);

            // CHARACTER_MAXIMUM_LENGTH is a CHARACTER count for every string type, national ones included -
            // it is CHARACTER_OCTET_LENGTH that reports the byte count and doubles for nvarchar. The value
            // is therefore directly comparable with the model's declared maximum length and must not be
            // halved; -1 is how the catalogue spells an unbounded (max) column and is passed through as
            // such so it can never be mistaken for a real width.
            int? characterLength = reader.IsDBNull(4)
                ? null
                : reader.GetInt32(4);

            columns[FormattableString.Invariant($"{table}.{column}")] =
                new CatalogueColumn(dataType, isNullable, characterLength);
        }

        return columns;
    }

    /// <summary>Builds a unique key of an exact length.</summary>
    /// <param name="length">The required length, at least as long as the random prefix.</param>
    /// <returns>A key whose length is exactly <paramref name="length"/>.</returns>
    /// <remarks>
    /// The random prefix keeps the value clear of the unique indexes over these columns while the padding
    /// pins the length, so a boundary assertion cannot be satisfied by a coincidentally short value.
    /// </remarks>
    private static string KeyOfLength(int length) =>
        (Suffix() + new string('K', length))[..length];

    /// <summary>Produces a short random suffix so concurrently executing suites cannot collide.</summary>
    /// <returns>A twelve-character suffix.</returns>
    private static string Suffix() => Guid.NewGuid().ToString("N")[..12];

    /// <summary>One column as the entity model binds it.</summary>
    /// <param name="Key">The <c>Table.Column</c> identity of the column.</param>
    /// <param name="IsNullable">Whether the model maps the column nullable.</param>
    /// <param name="MaxLength">The declared maximum length, or <see langword="null"/> when unconstrained.</param>
    private sealed record MappedColumn(string Key, bool IsNullable, int? MaxLength);

    /// <summary>One column as the provisioned database declares it.</summary>
    /// <param name="DataType">The catalogue data-type name.</param>
    /// <param name="IsNullable">Whether the database permits nulls in the column.</param>
    /// <param name="CharacterLength">
    /// The maximum length in CHARACTERS, or <see langword="null"/> for a column that carries none.
    /// </param>
    private sealed record CatalogueColumn(string DataType, bool IsNullable, int? CharacterLength);
}
