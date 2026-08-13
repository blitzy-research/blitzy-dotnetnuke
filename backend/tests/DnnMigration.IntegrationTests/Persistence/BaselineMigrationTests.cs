using System.Reflection;
using System.Text.RegularExpressions;
using DnnMigration.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DnnMigration.IntegrationTests.Persistence;

/// <summary>Proves that the baseline migration is inert: it records a history row and touches nothing else.</summary>
[Trait("Category", "Integration")]
[Collection(IntegrationTestCollection.Name)]
public sealed partial class BaselineMigrationTests
{
    /// <summary>The identifier the baseline migration must keep.</summary>
    /// <remarks>
    /// Asserted as a literal because it is a value written into <c>__EFMigrationsHistory</c> on every
    /// database this solution is applied to.
    /// </remarks>
    private const string BaselineMigrationId = "20260730120000_InitialCreate";

    /// <summary>
    /// The history table Entity Framework Core maintains, and the only table this migration writes.
    /// </summary>
    private const string HistoryTableName = "__EFMigrationsHistory";

    private readonly ApiTestFixture _fixture;

    /// <summary>Initialises a new instance of the <see cref="BaselineMigrationTests"/> class.</summary>
    /// <param name="fixture">The shared host, from which the persistence services are resolved.</param>
    public BaselineMigrationTests(ApiTestFixture fixture) => _fixture = fixture;

    /// <summary>The tooling discovers exactly one migration, under the identifier it must keep.</summary>
    [Fact]
    public void TheMigrationsAssembly_DeclaresOnlyTheBaselineMigration()
    {
        using IServiceScope scope = _fixture.Services.CreateScope();
        DnnDbContext context = scope.ServiceProvider.GetRequiredService<DnnDbContext>();
        IMigrationsAssembly migrations = context.GetService<IMigrationsAssembly>();

        migrations.Migrations.Keys.Should().BeEquivalentTo(
            new[] { BaselineMigrationId },
            "the baseline is the only migration this solution ships: a second one would mean the schema is "
            + "being versioned from here, which Rule T4 forbids while the legacy database owns it");

        migrations.Assembly.Should().BeSameAs(
            typeof(DnnDbContext).Assembly,
            "the migrations live beside the context, so the tooling needs no separate migrations assembly");
    }

    /// <summary>The materialised migration carries no operation in either direction.</summary>
    [Fact]
    public void TheBaselineMigration_CarriesNoOperationInEitherDirection()
    {
        using IServiceScope scope = _fixture.Services.CreateScope();
        DnnDbContext context = scope.ServiceProvider.GetRequiredService<DnnDbContext>();
        IMigrationsAssembly migrations = context.GetService<IMigrationsAssembly>();

        TypeInfo declaration = migrations.Migrations[BaselineMigrationId];
        Migration migration = migrations.CreateMigration(declaration, context.Database.ProviderName!);

        migration.UpOperations.Should().BeEmpty(
            "the only change applying this migration may make to an existing DotNetNuke database is the row "
            + "it inserts into the history table");
        migration.DownOperations.Should().BeEmpty(
            "reverting it must not alter a schema this solution does not own either");
    }

    /// <summary>The migration records the mapped entities as its target model.</summary>
    /// <remarks>
    /// The target model is what a future migration is diffed against, so an empty or stale one would make
    /// the first real migration generate the entire schema from scratch - which, against a live
    /// installation, is the worst possible output.
    /// </remarks>
    [Fact]
    public void TheBaselineMigration_RecordsTheMappedModelAsItsTarget()
    {
        using IServiceScope scope = _fixture.Services.CreateScope();
        DnnDbContext context = scope.ServiceProvider.GetRequiredService<DnnDbContext>();
        IMigrationsAssembly migrations = context.GetService<IMigrationsAssembly>();

        TypeInfo declaration = migrations.Migrations[BaselineMigrationId];
        Migration migration = migrations.CreateMigration(declaration, context.Database.ProviderName!);

        IReadOnlyList<string> recorded = migration.TargetModel.GetEntityTypes()
            .Select(entity => entity.GetTableName())
            .Where(table => !string.IsNullOrEmpty(table))
            .Select(table => table!)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        recorded.Should().BeEquivalentTo(
            TerminalSchema.Tables.Keys,
            "an empty or stale target model would make the next migration diff the whole schema into "
            + "existence against a database that already has every one of these tables");

        migrations.ModelSnapshot.Should().NotBeNull(
            "the snapshot beside the migration is the artefact the tooling diffs against, so its absence "
            + "would make the next generated migration recreate everything the target model records");
    }

    /// <summary>The script the tooling would run creates and writes the history table, and no other table.</summary>
    /// <remarks>
    /// Every table named by a <c>CREATE</c>, <c>ALTER</c> or <c>DROP TABLE</c> statement anywhere in the
    /// script must be the history table. Matching on the statement rather than on a list of forbidden names
    /// means a table added to the model later is covered without this test being edited, and it does not
    /// depend on whether the provider spells a name bare or schema-qualified.
    /// </remarks>
    [Fact]
    public void TheGeneratedScript_TouchesOnlyTheHistoryTable()
    {
        using IServiceScope scope = _fixture.Services.CreateScope();
        DnnDbContext context = scope.ServiceProvider.GetRequiredService<DnnDbContext>();

        string script = context.GetService<IMigrator>().GenerateScript(
            fromMigration: null,
            toMigration: BaselineMigrationId);

        script.Should().Contain(HistoryTableName, "the history row is the migration's entire effect");
        script.Should().Contain(
            BaselineMigrationId,
            "the identifier is written into the history table, and that record is what establishes the "
            + "baseline so no later migration tries to create the schema");

        IReadOnlyList<string> tablesTouched = TableDefinitionStatement().Matches(script)
            .Select(match => match.Groups["table"].Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        tablesTouched.Should().OnlyContain(
            table => string.Equals(table, HistoryTableName, StringComparison.OrdinalIgnoreCase),
            "the legacy database owns this schema: applying the baseline may create and write the history "
            + "table and must issue no data-definition language against any mapped table");

        foreach (string table in TerminalSchema.Tables.Keys)
        {
            tablesTouched.Should().NotContain(
                table,
                "a mapped table appearing in the baseline script means an operation was added to Up, which "
                + "would alter a schema this solution does not own");
        }
    }

    /// <summary>
    /// Matches a table-level data-definition statement and captures the table it names, with or without a
    /// schema qualifier and with or without bracket quoting.
    /// </summary>
    [GeneratedRegex(
        @"\b(?:CREATE|ALTER|DROP)\s+TABLE\s+(?:(?:\[[^\]]+\]|\w+)\s*\.\s*)?(?:\[(?<table>[^\]]+)\]|(?<table>\w+))",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 2000)]
    private static partial Regex TableDefinitionStatement();
}
