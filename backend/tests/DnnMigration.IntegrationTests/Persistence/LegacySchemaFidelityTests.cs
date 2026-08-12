using DnnMigration.Domain.Abstractions.Repositories;
using DnnMigration.Domain.Entities;
using DnnMigration.Domain.Enums;
using DnnMigration.Infrastructure;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DnnMigration.IntegrationTests.Persistence;

/// <summary>
/// Measures the entity model AND the provisioned integration database against an INDEPENDENT record of the
/// terminal legacy schema, rather than against each other.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why this suite exists, and why its oracle had to change.</strong> Every other suite here asks
/// whether the model and the repositories behave correctly against the fixture. This one asks whether the
/// FIXTURE is right. It used to answer that by comparing the composed model with
/// <c>INFORMATION_SCHEMA</c> - but the database is provisioned from <c>Schema/DnnSchema.sql</c>, and that
/// script described itself as having been emitted from the model with <c>dotnet ef dbcontext script</c>.
/// So the comparison was model against model with two extra steps: a model that is internally consistent
/// and uniformly WRONG satisfied every assertion in this file, and the fixture could not be evidence about
/// the schema it exists to reproduce.
/// </para>
/// <para>
/// The oracle is now <c>Schema/TerminalSchema.manifest</c>, derived by replaying the 83 versioned legacy
/// upgrade scripts in <c>Website/Providers/DataProviders/SqlDataProvider</c> and corroborated against the
/// Red Gate fresh-install snapshot that ships beside them. Nothing this solution emits contributed to it.
/// See <see cref="TerminalSchema"/> and the manifest's own header for the derivation, the citation for
/// every record, and the one point on which the two legacy authorities disagree.
/// </para>
/// <para>
/// <strong>Replacing the oracle immediately found two defects the circular comparison had always
/// passed</strong>, which is the entire argument for doing it:
/// </para>
/// <list type="number">
///   <item><description>
///     <c>Roles.PortalID</c> was provisioned NULL where the terminal schema declares it NOT NULL, so the
///     fixture accepted a role belonging to no portal - a row no installation can hold - and two
///     persistence tests asserted the listing behaviour of exactly such a row.
///   </description></item>
///   <item><description>
///     Seven primary keys were provisioned CLUSTERED where the terminal schema and the entity
///     configurations both declare them NONCLUSTERED.
///   </description></item>
/// </list>
/// <para>
/// <strong>Three directions, because no one of them is sufficient.</strong> The manifest is compared with
/// the MODEL (which catches a mapping that binds the wrong width or nullability), with the provisioned
/// CATALOGUE (which catches a fixture that accepts or refuses values production does not), and then the
/// values the terminal schema permits and an earlier state forbade are actually WRITTEN, because no
/// metadata comparison can prove that a write lands.
/// </para>
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

    /// <summary>SQL Server's error number for an attempt to store a null in a NOT NULL column.</summary>
    private const int CannotInsertNullErrorNumber = 515;

    /// <summary>A connection string used only to compose the model; nothing is opened with it.</summary>
    /// <remarks>
    /// The model is composed from configuration alone. Registering the infrastructure requires a connection
    /// string to be present, but building the model never opens a connection, so this value is deliberately
    /// unreachable rather than pointing at a real server.
    /// </remarks>
    private const string ModelOnlyConnectionString =
        "Server=(localdb)\\model-only;Database=DnnMigrationModelOnly;Integrated Security=true";

    /// <summary>
    /// Columns the model deliberately maps NULLABLE although the terminal schema declares them NOT NULL.
    /// </summary>
    /// <remarks>
    /// <para>
    /// There is exactly one, it is named rather than skipped, and the assertion runs in BOTH directions:
    /// the model must still map it nullable and the manifest must still declare it NOT NULL. A blanket
    /// exemption would let a second such divergence appear unnoticed; this one cannot change on either side
    /// without failing.
    /// </para>
    /// <para>
    /// MIGRATION: <c>Roles.PortalID</c>. The terminal column is <c>int NOT NULL</c>
    /// (<c>01.00.05.SqlDataProvider:2749</c>, corroborated by the fresh-install snapshot), so no
    /// installation can hold a role with no owning portal. The PROPERTY stays <c>int?</c> because the
    /// terminal listing procedure filters on <c>( R.PortalId = @PortalId OR R.PortalId is null )</c>
    /// (<c>04.08.00.SqlDataProvider:40</c>) and <c>RoleRepository.GetByPortalIdAsync</c> reproduces that
    /// predicate, which cannot be expressed over a non-nullable property. The mapping is therefore strictly
    /// more permissive than the column: reads cannot fail, and a write of null is refused by the database
    /// rather than by the model - which <see cref="Roles_RefusesARoleThatBelongsToNoPortal"/> proves. The
    /// null branch of that predicate is consequently unsatisfiable against a faithful installation, and no
    /// test may fabricate a row to exercise it.
    /// </para>
    /// </remarks>
    private static readonly IReadOnlySet<string> DeliberateNullabilityRelaxations =
        new HashSet<string>(StringComparer.Ordinal) { "Roles.PortalID" };

    /// <summary>
    /// Columns whose provisioned declaration has drifted from the terminal schema at least once.
    /// </summary>
    /// <remarks>
    /// The sweeping comparisons below would already fail if any of these regressed, but they report a list
    /// rather than a cause. These state the terminal fact individually so that a regression names the
    /// column, the expected shape and the script that established it.
    /// </remarks>
    public static TheoryData<string, string, int?, bool> PreviouslyDriftedColumns => new()
    {
        // 02.02.02:3800 creates the column with no NOT NULL clause and nothing alters it afterwards.
        { "PortalAlias.HTTPAlias", "nvarchar", 200, true },

        // 02.02.00:459 widens ControlKey from the baseline twenty characters.
        { "ModuleControls.ControlKey", "nvarchar", 50, true },

        // 04.06.00:397 enlarges the permission key field to varchar(50) not null.
        { "Permission.PermissionKey", "varchar", 50, false },

        // 01.00.05:2749 rebuilds the table with PortalID int NOT NULL and renames it over Roles.
        { "Roles.PortalID", "int", null, false },
    };

    /// <summary>The composed relational model, built once for the whole suite.</summary>
    /// <remarks>
    /// <para>
    /// Composed through the same entirely public route the sibling context suite uses: the infrastructure
    /// registration exposes <see cref="DbContextOptions"/>, whose <see cref="DbContextOptions.ContextType"/>
    /// resolves the context as a <see cref="DbContext"/>. The internal context type is never named, so
    /// <c>InternalsVisibleTo</c> is not required and Rule T3 is honoured.
    /// </para>
    /// <para>
    /// This is the DESIGN-TIME model rather than <c>DbContext.Model</c>, and the difference is not cosmetic:
    /// the run-time model is read-optimised and DISCARDS the annotations that only DDL needs, so asking a
    /// key whether it is clustered throws "the requested configuration is not stored in the read-optimized
    /// model". Established by running it. The design-time model is also the honest choice on merit, because
    /// it is exactly the model a migration would be diffed against, which is the artefact whose agreement
    /// with the legacy schema this suite exists to check.
    /// </para>
    /// </remarks>
    private static readonly IModel Model = ComposeModel();

    private readonly ApiTestFixture _fixture;

    /// <summary>Initialises a new instance of the <see cref="LegacySchemaFidelityTests"/> class.</summary>
    /// <param name="fixture">The shared host and provisioned database.</param>
    public LegacySchemaFidelityTests(ApiTestFixture fixture) => _fixture = fixture;

    /// <summary>The oracle loads completely, and its own totals agree with what was parsed.</summary>
    /// <remarks>
    /// <para>
    /// A comparison against a half-read manifest still passes, which is the failure mode that would make
    /// every other test in this file meaningless. <see cref="TerminalSchema"/> already refuses to load a
    /// resource whose <c>TOTALS</c> record disagrees with the parsed counts; this states the expected size
    /// explicitly as well, so that DELETING records from the manifest and adjusting its totals to match -
    /// the one edit that would defeat the load-time guard - fails here.
    /// </para>
    /// <para>
    /// The structural assertions that follow are the ones a hand-edit is most likely to break: a table with
    /// no columns, a foreign key pointing at a table the manifest does not describe, or an index over a
    /// column that does not exist.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheOracle_IsCompleteAndInternallyConsistent()
    {
        TerminalSchema.DeclaredTotals.Should().Be(
            new TerminalSchemaTotals(21, 194, 33, 29),
            "the terminal schema of the mapped tables is a fixed, measured quantity, and a manifest that "
            + "has shrunk is asserting less than it appears to");

        TerminalSchema.Tables.Should().HaveCount(21);
        TerminalSchema.Columns.Should().HaveCount(194);

        foreach ((string name, TerminalTable table) in TerminalSchema.Tables)
        {
            TerminalSchema.ColumnsOf(name).Should().NotBeEmpty(
                "a table with no columns describes nothing");
            table.PrimaryKeyColumns.Should().NotBeEmpty("every mapped table has a primary key");

            foreach (string column in table.PrimaryKeyColumns)
            {
                TerminalSchema.Columns.Should().ContainKey(
                    FormattableString.Invariant($"{name}.{column}"),
                    "a key column that the manifest does not describe cannot be compared");
            }
        }

        foreach (TerminalIndex index in TerminalSchema.Indexes.Values)
        {
            TerminalSchema.Tables.Should().ContainKey(index.Table);

            foreach (string column in index.Columns)
            {
                TerminalSchema.Columns.Should().ContainKey(
                    FormattableString.Invariant($"{index.Table}.{column}"));
            }
        }

        foreach (TerminalForeignKey foreignKey in TerminalSchema.ForeignKeys.Values)
        {
            TerminalSchema.Tables.Should().ContainKey(foreignKey.Table);
            TerminalSchema.Tables.Should().ContainKey(foreignKey.PrincipalTable);
            foreignKey.Columns.Should().HaveSameCount(
                foreignKey.PrincipalColumns,
                "a foreign key relates equal numbers of columns");
        }

        TerminalSchema.Columns.Values.Select(column => column.Provenance).Should().OnlyContain(
            provenance => provenance.Contains(':', StringComparison.Ordinal),
            "every record cites the legacy script and line that established it, so that a disagreement "
            + "can be settled by reading the chain rather than by argument");
    }

    /// <summary>The model binds exactly the tables the terminal schema declares - no more, no fewer.</summary>
    [Fact]
    public void Model_MapsExactlyTheTablesTheTerminalSchemaDeclares()
    {
        IEnumerable<string> mapped = Model.GetEntityTypes()
            .Select(entity => entity.GetTableName())
            .Where(table => !string.IsNullOrEmpty(table))
            .Select(table => table!)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(table => table, StringComparer.Ordinal);

        mapped.Should().Equal(
            TerminalSchema.Tables.Keys.OrderBy(table => table, StringComparer.Ordinal),
            "an entity bound to a table the terminal schema does not have reads an installation as empty, "
            + "and a terminal table nothing maps is a capability silently absent from this migration");
    }

    /// <summary>The model binds exactly the columns the terminal schema declares on those tables.</summary>
    /// <remarks>
    /// Both directions matter and they fail differently. A mapped column the terminal schema does not have
    /// makes every query against a real installation fail with an invalid-column-name error. A terminal
    /// column nothing maps is invisible instead: reads succeed, writes succeed, and the value is silently
    /// never carried.
    /// </remarks>
    [Fact]
    public void Model_MapsExactlyTheColumnsTheTerminalSchemaDeclares()
    {
        IReadOnlyList<MappedColumn> mapped = EnumerateMappedColumns();

        mapped.Select(column => column.Key).Should().OnlyHaveUniqueItems();

        mapped.Select(column => column.Key).OrderBy(key => key, StringComparer.Ordinal)
            .Should().Equal(
                TerminalSchema.Columns.Keys.OrderBy(key => key, StringComparer.Ordinal),
                "the legacy spelling is the binding, so a case difference is as much a failure as a "
                + "missing column: Users.UserID and UserPortals.UserId spell the same logical key "
                + "differently and the schema means both literally");
    }

    /// <summary>Every mapped column is bound at the terminal store type and declared width.</summary>
    /// <remarks>
    /// A model narrower than the column refuses legacy-valid data; a model wider than the column defers the
    /// refusal to the database, where it arrives as a store failure rather than as validation. The store
    /// type is asserted as well as the width because it is what stops a value converter being introduced
    /// between a decimal property and a monetary column, which would make the stored value depend on the
    /// writing server's culture.
    /// </remarks>
    [Fact]
    public void Model_BindsEveryColumnAtItsTerminalTypeAndWidth()
    {
        var mismatches = new List<string>();

        foreach (MappedColumn column in EnumerateMappedColumns())
        {
            TerminalColumn terminal = TerminalSchema.Columns[column.Key];

            if (!string.Equals(column.StoreTypeBase, terminal.DataType, StringComparison.OrdinalIgnoreCase))
            {
                mismatches.Add(FormattableString.Invariant(
                    $"{column.Key}: model binds {column.StoreTypeBase}, terminal is {terminal.DataType} [{terminal.Provenance}]"));
                continue;
            }

            if (column.StoreLength != terminal.MaxLength)
            {
                mismatches.Add(FormattableString.Invariant(
                    $"{column.Key}: model width {Describe(column.StoreLength)}, terminal width {Describe(terminal.MaxLength)} [{terminal.Provenance}]"));
            }
        }

        mismatches.Should().BeEmpty(
            "the terminal state is the only meaningful one - the upgrade chain widens, retypes and rebuilds "
            + "across 83 scripts - so a declaration copied from a baseline CREATE TABLE is wrong by "
            + "construction even though it looks authoritative");
    }

    /// <summary>
    /// Every mapped column is nullable exactly where the terminal schema is, apart from one named and
    /// justified relaxation.
    /// </summary>
    /// <remarks>
    /// A model that maps a column nullable while the schema declares it NOT NULL defers a refusal to the
    /// database; a model that maps a column required while the schema permits nulls fails only when an
    /// existing null is read, and then it fails by throwing rather than by returning. Both are invisible to
    /// a build, to a migration diff and to every test that happens to supply a value, which is precisely
    /// how the alias defect survived for as long as it did.
    /// </remarks>
    [Fact]
    public void Model_MapsNullabilityExactlyWhereTheTerminalSchemaDoes()
    {
        var mismatches = new List<string>();
        var relaxationsSeen = new List<string>();

        foreach (MappedColumn column in EnumerateMappedColumns())
        {
            TerminalColumn terminal = TerminalSchema.Columns[column.Key];

            if (column.IsNullable == terminal.IsNullable)
            {
                continue;
            }

            if (DeliberateNullabilityRelaxations.Contains(column.Key)
                && column.IsNullable
                && !terminal.IsNullable)
            {
                relaxationsSeen.Add(column.Key);
                continue;
            }

            mismatches.Add(FormattableString.Invariant(
                $"{column.Key}: model nullable={column.IsNullable}, terminal nullable={terminal.IsNullable} [{terminal.Provenance}]"));
        }

        mismatches.Should().BeEmpty(
            "a nullability disagreement is either a stale schema declaration or a mis-mapped configuration, "
            + "and never an acceptable difference");

        relaxationsSeen.Should().BeEquivalentTo(
            DeliberateNullabilityRelaxations,
            "each documented relaxation must still BE one: if the model tightened, or the terminal column "
            + "was found to be nullable after all, the exemption is no longer describing reality and the "
            + "reasoning recorded against it has to be revisited rather than left standing");
    }

    /// <summary>
    /// The model declares each primary key with the terminal name, key columns and physical topology.
    /// </summary>
    /// <remarks>
    /// The name and the columns are the part a query depends on. The topology is the part a migration diff
    /// depends on: SQL Server defaults a primary key to CLUSTERED, so a bare declaration would put a
    /// physical layout into the model that seven of these tables do not have, and the model snapshot is
    /// exactly what a future migration would be diffed against.
    /// </remarks>
    [Fact]
    public void Model_DeclaresThePrimaryKeysTheTerminalSchemaDeclares()
    {
        var mismatches = new List<string>();

        foreach (IEntityType entity in Model.GetEntityTypes())
        {
            string? tableName = entity.GetTableName();

            if (string.IsNullOrEmpty(tableName))
            {
                continue;
            }

            StoreObjectIdentifier table = StoreObjectIdentifier.Table(tableName, entity.GetSchema());
            TerminalTable terminal = TerminalSchema.Tables[tableName];
            IKey? key = entity.FindPrimaryKey();

            if (key is null)
            {
                mismatches.Add(FormattableString.Invariant($"{tableName}: the model declares no key"));
                continue;
            }

            string name = key.GetName() ?? string.Empty;

            if (!string.Equals(name, terminal.PrimaryKeyName, StringComparison.Ordinal))
            {
                mismatches.Add(FormattableString.Invariant(
                    $"{tableName}: model key '{name}', terminal key '{terminal.PrimaryKeyName}' [{terminal.Provenance}]"));
            }

            IEnumerable<string> columns = key.Properties.Select(property =>
                property.GetColumnName(table) ?? property.Name);

            if (!columns.SequenceEqual(terminal.PrimaryKeyColumns, StringComparer.Ordinal))
            {
                mismatches.Add(FormattableString.Invariant(
                    $"{tableName}: model key columns ({string.Join(", ", columns)}), terminal ({string.Join(", ", terminal.PrimaryKeyColumns)})"));
            }

            // A key that says nothing about clustering takes the provider default, and for SQL Server
            // that default is CLUSTERED - so an unconfigured key is a positive claim, not an absence.
            bool clustered = key.IsClustered() ?? true;

            if (clustered != terminal.IsClustered)
            {
                mismatches.Add(FormattableString.Invariant(
                    $"{tableName}: model clustered={clustered}, terminal clustered={terminal.IsClustered} [{terminal.Provenance}]"));
            }
        }

        mismatches.Should().BeEmpty(
            "the primary key carries the legacy constraint name, the legacy key order and the legacy "
            + "topology, and all three are part of the schema this migration binds to rather than "
            + "implementation detail");
    }

    /// <summary>
    /// The provisioned database declares every mapped column at its terminal type, width and nullability.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// This is the direction the old oracle could not report on honestly, and the one that found the two
    /// drifted declarations. A fixture narrower than the terminal schema rejects legacy-valid data with a
    /// truncation error that reads like a test-data mistake; a fixture wider than it lets a test store a
    /// value a real installation refuses; and a fixture that relaxes a NOT NULL lets a test assert
    /// behaviour over a row that cannot exist.
    /// </remarks>
    [Fact]
    public async Task ProvisionedDatabase_ReproducesEveryTerminalColumnDeclaration()
    {
        IReadOnlyDictionary<string, CatalogueColumn> catalogue = await ReadCatalogueColumnsAsync();
        var mismatches = new List<string>();

        foreach ((string key, TerminalColumn terminal) in TerminalSchema.Columns)
        {
            if (!catalogue.TryGetValue(key, out CatalogueColumn? provisioned))
            {
                mismatches.Add(FormattableString.Invariant(
                    $"{key}: absent from the database, terminal declares {terminal.Declaration()} [{terminal.Provenance}]"));
                continue;
            }

            if (!string.Equals(provisioned.DataType, terminal.DataType, StringComparison.OrdinalIgnoreCase))
            {
                mismatches.Add(FormattableString.Invariant(
                    $"{key}: database type {provisioned.DataType}, terminal {terminal.DataType} [{terminal.Provenance}]"));
            }

            if (provisioned.IsNullable != terminal.IsNullable)
            {
                mismatches.Add(FormattableString.Invariant(
                    $"{key}: database nullable={provisioned.IsNullable}, terminal nullable={terminal.IsNullable} [{terminal.Provenance}]"));
            }

            // A legacy large-object type carries NO declared width - the DDL spells it "ntext" with no
            // specifier - but the catalogue reports the type's own capacity for one anyway (1073741823 for
            // ntext, 2147483647 for text and image). Comparing that against "no width" would report every
            // such column as a mismatch, so the width comparison is skipped for exactly those types and the
            // type name, which IS compared above, is what pins them. Established by running it: Modules.
            // Footer was the first to report it.
            if (!IsLegacyLargeObjectType(terminal.DataType) && provisioned.CharacterLength != terminal.MaxLength)
            {
                mismatches.Add(FormattableString.Invariant(
                    $"{key}: database width {Describe(provisioned.CharacterLength)}, terminal width {Describe(terminal.MaxLength)} [{terminal.Provenance}]"));
            }
        }

        mismatches.Should().BeEmpty(
            "the fixture exists to reproduce the terminal legacy schema, measured against the upgrade "
            + "chain rather than against the model that reads it");
    }

    /// <summary>The provisioned database seeds every identity column exactly as the terminal schema does.</summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// <para>
    /// Three of these seeds are load-bearing rather than incidental, and they are why the whole inventory is
    /// asserted instead of a sample. A tenant key of -1 collides with the legacy "absent integer" marker,
    /// and a role, page or module key of 0 collides with the CLR default for an unassigned integer. Both
    /// collisions have already produced real defects in this migration.
    /// </para>
    /// <para>
    /// The seed is asserted against the DATABASE rather than against the model because that is where it has
    /// meaning: Entity Framework never uses a seed at run time, it only emits one when generating DDL, and
    /// this migration generates none.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ProvisionedDatabase_ReproducesEveryTerminalIdentitySeed()
    {
        IReadOnlyDictionary<string, (int Seed, int Increment)> provisioned = await ReadIdentityColumnsAsync();

        IReadOnlyDictionary<string, string> expected = TerminalSchema.Columns.Values
            .Where(column => column.Identity is not null)
            .ToDictionary(
                column => column.Key,
                column => FormattableString.Invariant(
                    $"{column.Identity!.Value.Seed},{column.Identity!.Value.Increment}"),
                StringComparer.Ordinal);

        expected.Should().HaveCount(19, "nineteen of the mapped columns are identities");

        IReadOnlyDictionary<string, string> actual = provisioned.ToDictionary(
            pair => pair.Key,
            pair => FormattableString.Invariant($"{pair.Value.Seed},{pair.Value.Increment}"),
            StringComparer.Ordinal);

        actual.Should().Equal(
            expected,
            "an identity the database seeds differently from the terminal schema changes which keys are "
            + "reachable, and does so silently: the first row simply gets a number no installation would "
            + "have given it");
    }

    /// <summary>
    /// The provisioned database declares every primary key with the terminal name, order and topology.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// The catalogue read here deliberately includes the clustering, because that is the second defect
    /// measuring against the manifest found: the fixture declared every key without a topology, which SQL
    /// Server reads as CLUSTERED, while seven of these tables are heaps in the terminal schema.
    /// </remarks>
    [Fact]
    public async Task ProvisionedDatabase_ReproducesEveryTerminalPrimaryKey()
    {
        IReadOnlyDictionary<string, CataloguePrimaryKey> provisioned = await ReadPrimaryKeysAsync();
        var mismatches = new List<string>();

        foreach ((string table, TerminalTable terminal) in TerminalSchema.Tables)
        {
            if (!provisioned.TryGetValue(table, out CataloguePrimaryKey? actual))
            {
                mismatches.Add(FormattableString.Invariant($"{table}: no primary key is provisioned"));
                continue;
            }

            if (!string.Equals(actual.Name, terminal.PrimaryKeyName, StringComparison.Ordinal))
            {
                mismatches.Add(FormattableString.Invariant(
                    $"{table}: database key '{actual.Name}', terminal '{terminal.PrimaryKeyName}' [{terminal.Provenance}]"));
            }

            if (!actual.Columns.SequenceEqual(terminal.PrimaryKeyColumns, StringComparer.Ordinal))
            {
                mismatches.Add(FormattableString.Invariant(
                    $"{table}: database key columns ({string.Join(", ", actual.Columns)}), terminal ({string.Join(", ", terminal.PrimaryKeyColumns)})"));
            }

            if (actual.IsClustered != terminal.IsClustered)
            {
                mismatches.Add(FormattableString.Invariant(
                    $"{table}: database clustered={actual.IsClustered}, terminal clustered={terminal.IsClustered} [{terminal.Provenance}]"));
            }
        }

        mismatches.Should().BeEmpty(
            "the provisioned key inventory must match the terminal one by name, by order and by topology");
    }

    /// <summary>Each column whose declaration has drifted before is provisioned at its terminal shape.</summary>
    /// <param name="key">The <c>Table.Column</c> identity.</param>
    /// <param name="dataType">The terminal store type.</param>
    /// <param name="maxLength">The terminal declared width, or <see langword="null"/>.</param>
    /// <param name="isNullable">Whether the terminal column admits nulls.</param>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// The expected values are restated here as literals rather than read from the manifest, deliberately.
    /// Every other comparison in this file is manifest-driven, so a manifest edited to make a failure go
    /// away would silence all of them at once; these four cases are the ones that have actually drifted,
    /// and they fail independently of the oracle.
    /// </remarks>
    [Theory]
    [Trait("Category", "Integration")]
    [MemberData(nameof(PreviouslyDriftedColumns))]
    public async Task TheColumnsThatDriftedBefore_AreProvisionedAtTheirTerminalShapes(
        string key,
        string dataType,
        int? maxLength,
        bool isNullable)
    {
        IReadOnlyDictionary<string, CatalogueColumn> catalogue = await ReadCatalogueColumnsAsync();

        catalogue.Should().ContainKey(key);

        CatalogueColumn provisioned = catalogue[key];
        TerminalColumn terminal = TerminalSchema.Columns[key];

        provisioned.DataType.Should().Be(dataType);
        provisioned.CharacterLength.Should().Be(maxLength);
        provisioned.IsNullable.Should().Be(isNullable);

        // And the oracle still says the same thing, so the two cannot drift apart quietly either.
        terminal.DataType.Should().Be(dataType);
        terminal.MaxLength.Should().Be(maxLength);
        terminal.IsNullable.Should().Be(isNullable);
    }

    /// <summary>
    /// A role with no owning portal is refused by the store, because the terminal column forbids it.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// <para>
    /// This is the behavioural half of the <c>Roles.PortalID</c> correction. The fixture used to declare the
    /// column NULL, so such a row could be written, and two persistence tests were built on top of one.
    /// Asserting the refusal is what stops that fabrication returning: it is the only form in which the
    /// constraint can be exercised, since the mapping is deliberately more permissive than the column.
    /// </para>
    /// <para>
    /// The refusal must arrive from the DATABASE - error 515, "cannot insert the value NULL" - rather than
    /// from a validator or from the model, because the point is that the provisioned schema now carries the
    /// constraint a real installation carries. <c>SaveChangesAsync</c> translates only duplicate-key
    /// violations, so a null violation surfaces as a plain <see cref="DbUpdateException"/>.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Roles_RefusesARoleThatBelongsToNoPortal()
    {
        string roleName = FormattableString.Invariant($"Portal Less {Suffix()}");
        Role role = new()
        {
            PortalId = null,
            RoleName = roleName,
            Description = "Written by the schema fidelity suite to prove the terminal constraint.",
        };

        using IServiceScope scope = _fixture.Services.CreateScope();
        IRoleRepository roles = scope.ServiceProvider.GetRequiredService<IRoleRepository>();
        IUnitOfWork unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        await roles.AddAsync(role);

        Func<Task> write = async () => await unitOfWork.SaveChangesAsync();

        DbUpdateException failure = (await write.Should().ThrowAsync<DbUpdateException>(
            "Roles.PortalID is NOT NULL in the terminal schema (01.00.05.SqlDataProvider:2749), so a role "
            + "belonging to no portal is a row no installation can hold")).Which;

        failure.GetBaseException().Should().BeOfType<SqlException>()
            .Which.Number.Should().Be(
                CannotInsertNullErrorNumber,
                "the refusal comes from the column itself rather than from application validation");

        int stored = await _fixture.Database.ScalarAsync<int>(
            "SELECT COUNT(*) FROM [dbo].[Roles] WHERE [RoleName] = @roleName",
            new Dictionary<string, object?> { ["roleName"] = roleName });

        stored.Should().Be(0, "a refused write leaves nothing behind, so nothing needs cleaning up");
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

        // The design-time model, for the reason recorded on the Model field. It is read here rather than
        // outside the scope because the service it comes from belongs to the context's internal provider.
        return context.GetService<IDesignTimeModel>().Model;
    }

    /// <summary>Projects every mapped column of the model into a comparable shape.</summary>
    /// <returns>One entry per column the model binds, keyed <c>Table.Column</c>.</returns>
    /// <remarks>
    /// A property the model deliberately does not map has no column name for its table and is skipped, which
    /// keeps an ignored property from being reported as an absent column. The store type is taken from the
    /// configured column type where there is one and from the resolved relational type mapping otherwise,
    /// so a column that inherits the provider's default type is compared rather than silently exempted.
    /// </remarks>
    private static IReadOnlyList<MappedColumn> EnumerateMappedColumns()
    {
        var columns = new List<MappedColumn>();

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

                string storeType = property.GetColumnType(table)
                    ?? property.FindRelationalTypeMapping(table)?.StoreType
                    ?? throw new InvalidOperationException(
                        FormattableString.Invariant($"{tableName}.{columnName} resolves to no store type.")
                        + " It cannot be compared with the terminal schema.");

                (string baseType, int? length) = SplitStoreType(storeType);

                columns.Add(new MappedColumn(
                    FormattableString.Invariant($"{tableName}.{columnName}"),
                    property.IsColumnNullable(table),
                    baseType,
                    length));
            }
        }

        return columns;
    }

    /// <summary>Splits a store type such as <c>nvarchar(200)</c> into its name and declared width.</summary>
    /// <param name="storeType">The store type as the provider spells it.</param>
    /// <returns>The type name and the declared width, if it carries one.</returns>
    /// <remarks>
    /// <c>(max)</c> is reported as -1, which is exactly how <c>INFORMATION_SCHEMA</c> spells an unbounded
    /// column, so the two remain directly comparable and neither can be mistaken for a real width.
    /// </remarks>
    private static (string BaseType, int? Length) SplitStoreType(string storeType)
    {
        int open = storeType.IndexOf('(', StringComparison.Ordinal);

        if (open < 0)
        {
            return (storeType.Trim(), null);
        }

        string name = storeType[..open].Trim();
        string specifier = storeType[(open + 1)..].TrimEnd(')').Trim();

        if (string.Equals(specifier, "max", StringComparison.OrdinalIgnoreCase))
        {
            return (name, -1);
        }

        // A scaled numeric specifier such as "decimal(19, 4)" carries no character length, and the mapped
        // set holds none - money is the only non-integer numeric type here and it takes no specifier.
        return specifier.Contains(',', StringComparison.Ordinal)
            ? (name, null)
            : (name, int.Parse(specifier, System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>Reports whether a store type is one of the legacy large-object types.</summary>
    /// <param name="dataType">The store type name.</param>
    /// <returns><see langword="true"/> for <c>ntext</c>, <c>text</c> and <c>image</c>.</returns>
    /// <remarks>
    /// These three are the pre-2005 large-object types the legacy schema still uses, and they are declared
    /// without a length. The catalogue nonetheless reports the type's capacity as a character maximum, which
    /// is a property of the type rather than of the declaration.
    /// </remarks>
    private static bool IsLegacyLargeObjectType(string dataType) =>
        dataType.Equals("ntext", StringComparison.OrdinalIgnoreCase)
        || dataType.Equals("text", StringComparison.OrdinalIgnoreCase)
        || dataType.Equals("image", StringComparison.OrdinalIgnoreCase);

    /// <summary>Renders a width for a failure message.</summary>
    /// <param name="length">The width, if any.</param>
    /// <returns>The width, or "none".</returns>
    private static string Describe(int? length) =>
        length is int value ? value.ToString(System.Globalization.CultureInfo.InvariantCulture) : "none";

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
            // is therefore directly comparable with the terminal declared width and must not be halved; -1
            // is how the catalogue spells an unbounded (max) column and is passed through as such so it can
            // never be mistaken for a real width.
            int? characterLength = reader.IsDBNull(4)
                ? null
                : reader.GetInt32(4);

            columns[FormattableString.Invariant($"{table}.{column}")] =
                new CatalogueColumn(dataType, isNullable, characterLength);
        }

        return columns;
    }

    /// <summary>Reads every identity column of the provisioned <c>dbo</c> schema with its seed.</summary>
    /// <returns>The seed and increment of each identity column, keyed <c>Table.Column</c>.</returns>
    /// <remarks>
    /// Only the mapped tables are reported, because the provisioning scripts also create the external
    /// membership objects and this comparison is about the mapped set.
    /// </remarks>
    private async Task<IReadOnlyDictionary<string, (int Seed, int Increment)>> ReadIdentityColumnsAsync()
    {
        const string query =
            """
            SELECT
                t.[name] AS [TableName],
                c.[name] AS [ColumnName],
                CAST(c.[seed_value] AS int) AS [Seed],
                CAST(c.[increment_value] AS int) AS [Increment]
            FROM sys.identity_columns AS c
            INNER JOIN sys.tables AS t ON t.[object_id] = c.[object_id]
            INNER JOIN sys.schemas AS s ON s.[schema_id] = t.[schema_id]
            WHERE s.[name] = N'dbo'
            ORDER BY t.[name], c.[name];
            """;

        var identities = new Dictionary<string, (int Seed, int Increment)>(StringComparer.Ordinal);

        await using var connection = new SqlConnection(_fixture.Database.ConnectionString);
        await connection.OpenAsync();

        await using var command = new SqlCommand(query, connection);
        await using SqlDataReader reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            string table = reader.GetString(0);

            if (!TerminalSchema.Tables.ContainsKey(table))
            {
                continue;
            }

            identities[FormattableString.Invariant($"{table}.{reader.GetString(1)}")] =
                (reader.GetInt32(2), reader.GetInt32(3));
        }

        return identities;
    }

    /// <summary>Reads the provisioned primary key of every mapped table.</summary>
    /// <returns>The key of each mapped table, keyed by table name.</returns>
    private async Task<IReadOnlyDictionary<string, CataloguePrimaryKey>> ReadPrimaryKeysAsync()
    {
        const string query =
            """
            SELECT
                t.[name] AS [TableName],
                i.[name] AS [KeyName],
                STRING_AGG(c.[name], N',') WITHIN GROUP (ORDER BY ic.[key_ordinal]) AS [Columns],
                i.[type_desc] AS [Topology]
            FROM sys.indexes AS i
            INNER JOIN sys.tables AS t ON t.[object_id] = i.[object_id]
            INNER JOIN sys.schemas AS s ON s.[schema_id] = t.[schema_id]
            INNER JOIN sys.index_columns AS ic
                ON ic.[object_id] = i.[object_id]
                AND ic.[index_id] = i.[index_id]
                AND ic.[key_ordinal] > 0
            INNER JOIN sys.columns AS c
                ON c.[object_id] = ic.[object_id]
                AND c.[column_id] = ic.[column_id]
            WHERE s.[name] = N'dbo' AND i.[is_primary_key] = 1
            GROUP BY t.[name], i.[name], i.[type_desc]
            ORDER BY t.[name];
            """;

        var keys = new Dictionary<string, CataloguePrimaryKey>(StringComparer.Ordinal);

        await using var connection = new SqlConnection(_fixture.Database.ConnectionString);
        await connection.OpenAsync();

        await using var command = new SqlCommand(query, connection);
        await using SqlDataReader reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            string table = reader.GetString(0);

            if (!TerminalSchema.Tables.ContainsKey(table))
            {
                continue;
            }

            keys[table] = new CataloguePrimaryKey(
                reader.GetString(1),
                reader.GetString(2).Split(',', StringSplitOptions.TrimEntries),
                string.Equals(reader.GetString(3), "CLUSTERED", StringComparison.Ordinal));
        }

        return keys;
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
    /// <param name="StoreTypeBase">The store type name, without any length specifier.</param>
    /// <param name="StoreLength">The declared width, or <see langword="null"/> when the type carries none.</param>
    private sealed record MappedColumn(string Key, bool IsNullable, string StoreTypeBase, int? StoreLength);

    /// <summary>One column as the provisioned database declares it.</summary>
    /// <param name="DataType">The catalogue data-type name.</param>
    /// <param name="IsNullable">Whether the database permits nulls in the column.</param>
    /// <param name="CharacterLength">
    /// The maximum length in CHARACTERS, or <see langword="null"/> for a column that carries none.
    /// </param>
    private sealed record CatalogueColumn(string DataType, bool IsNullable, int? CharacterLength);

    /// <summary>One primary key as the provisioned database declares it.</summary>
    /// <param name="Name">The constraint name.</param>
    /// <param name="Columns">The key columns, in key order.</param>
    /// <param name="IsClustered">Whether the key is clustered.</param>
    private sealed record CataloguePrimaryKey(string Name, IReadOnlyList<string> Columns, bool IsClustered);
}
