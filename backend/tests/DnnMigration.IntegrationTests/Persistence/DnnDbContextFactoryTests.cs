using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Reflection;
using DnnMigration.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Xunit;

namespace DnnMigration.IntegrationTests.Persistence;

/// <summary>
/// Exercises the design-time context factory the Entity Framework Core tooling activates.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why this type had no test.</strong> Nothing in the application ever calls it. It exists solely so
/// that <c>dotnet ef</c> can obtain a context without starting the API, which means the only thing that ever
/// exercised it was a developer running a command by hand - and a fault in it does not fail a build, a test
/// run or a deployment. It fails the one moment it is used, on a developer machine, with the tooling's
/// generic "unable to create an object of type 'DnnDbContext'" message that names nothing about the actual
/// cause.
/// </para>
/// <para>
/// <strong>What is actually at stake is bigger than a convenience.</strong> This factory decides which
/// database the tooling talks to. It reads <c>ConnectionStrings__Default</c>, which on any deployed host is
/// the PRODUCTION database - <c>docker/docker-compose.yml</c> sets exactly that variable on the API
/// container. A factory that silently ignored the variable and fell back to the local design-time database
/// would make <c>dotnet ef migrations</c> quietly operate on the wrong catalogue; one that fell back the
/// other way, or that opened a connection or ran a query while merely being constructed, would reach a
/// production database from a command a developer believed was offline. The assertions below pin both
/// halves: the environment wins when it says something, the local fallback applies when it says nothing, and
/// in neither case is anything contacted.
/// </para>
/// <para>
/// The factory is reached by name rather than by reflection because the test assembly holds the
/// <c>InternalsVisibleTo</c> grant for the Infrastructure assembly. Its DISCOVERABILITY by the tooling - the
/// part reflection is genuinely the subject of - is asserted separately and explicitly.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
[Collection(IntegrationTestCollection.Name)]
public sealed class DnnDbContextFactoryTests
{
    /// <summary>The variable the factory reads, in the double-underscore form a container supplies.</summary>
    private const string ConnectionStringVariable = "ConnectionStrings__Default";

    /// <summary>The provider the tooling must be given, spelled as Entity Framework Core reports it.</summary>
    private const string SqlServerProviderName = "Microsoft.EntityFrameworkCore.SqlServer";

    /// <summary>
    /// The connection string the factory falls back to, asserted as a literal.
    /// </summary>
    /// <remarks>
    /// Duplicating the production constant here is deliberate. This value decides which catalogue a developer
    /// running <c>dotnet ef</c> with no environment variable set operates on, so a change to it must be a
    /// visible, deliberate edit in two places rather than a one-line change that silently repoints the
    /// tooling. The properties that make it safe - a local instance, its own database name, and no credential
    /// of any kind - are asserted separately, so this is not merely an echo.
    /// </remarks>
    private const string DesignTimeFallbackConnectionString =
        "Server=(localdb)\\MSSQLLocalDB;Database=DnnMigrationDesignTime;"
        + "Trusted_Connection=True;TrustServerCertificate=True;";

    /// <summary>The factory is the type the tooling looks for, and can be activated the way it activates it.</summary>
    /// <remarks>
    /// The tooling scans the assembly for a type implementing the closed interface and creates it with a
    /// parameterless constructor. Each of those three facts is a separate way the factory can become
    /// undiscoverable without any compiler complaining: a differently closed interface, a constructor that
    /// grew a parameter, or a move into another assembly.
    /// </remarks>
    [Fact]
    public void TheFactory_IsDiscoverableAndActivatableByTheTooling()
    {
        Type factoryType = typeof(DnnDbContextFactory);

        factoryType.Should().BeAssignableTo<IDesignTimeDbContextFactory<DnnDbContext>>(
            "the tooling finds the factory by looking for this closed interface, and a factory closed over "
            + "any other context would be ignored without a compiler complaint");

        factoryType.Assembly.Should().BeSameAs(
            typeof(DnnDbContext).Assembly,
            "the tooling searches the assembly that holds the context, so a factory moved elsewhere would "
            + "never be found");

        ConstructorInfo? constructor = factoryType.GetConstructor(Type.EmptyTypes);

        constructor.Should().NotBeNull(
            "the tooling activates the factory with no arguments, so a constructor that grew a parameter "
            + "would break the command and nothing else");
        constructor!.IsPublic.Should().BeTrue(
            "a public constructor on an internal type is still unreachable outside the assembly, and it is "
            + "what the tooling's activation requires");

        object? activated = Activator.CreateInstance(factoryType);

        activated.Should().BeAssignableTo<IDesignTimeDbContextFactory<DnnDbContext>>(
            "activating the factory exactly as the tooling does must yield something the tooling can use");
    }

    /// <summary>The factory hands the tooling a SQL Server context.</summary>
    /// <remarks>
    /// The provider decides the SQL a generated migration would contain. A context configured for any other
    /// provider - or for none, which throws only when the context is first used - would produce a script that
    /// cannot be applied to the DotNetNuke database.
    /// </remarks>
    [Fact]
    public void CreateDbContext_SelectsTheSqlServerProvider()
    {
        using DnnDbContext context = new DnnDbContextFactory().CreateDbContext([]);

        context.Database.ProviderName.Should().Be(
            SqlServerProviderName,
            "the DotNetNuke schema lives on SQL Server, and a migration generated for another provider "
            + "would emit SQL that cannot be applied to it");
    }

    /// <summary>The environment variable wins when it names a connection string.</summary>
    /// <remarks>
    /// This is the half that matters operationally: it is how a developer, and a deployment pipeline, point
    /// the tooling at a specific catalogue. The value asserted here is an unreachable loopback address, so
    /// the assertion cannot pass by accident against a database that happens to exist.
    /// </remarks>
    [Fact]
    public void CreateDbContext_UsesTheConnectionStringTheEnvironmentSupplies()
    {
        string supplied = RefusedEndpoint.ConnectionString("DesignTimeOverride");

        using IDisposable environment = ApiTestFixture.OverrideEnvironment(
            new Dictionary<string, string?> { [ConnectionStringVariable] = supplied });

        using DnnDbContext context = new DnnDbContextFactory().CreateDbContext([]);

        context.Database.GetConnectionString().Should().Be(
            supplied,
            "ignoring the variable would make the tooling operate on the local design-time database while "
            + "the operator believed it was working against the catalogue they named");
    }

    /// <summary>An absent, empty or whitespace value falls back to the local design-time database.</summary>
    /// <remarks>
    /// All three spellings of "says nothing" are covered because the factory tests for whitespace rather than
    /// only for null, and an empty variable is the shape a container supplies when a compose file interpolates
    /// an unset value. Falling THROUGH to an empty connection string would fail later with a message about
    /// the connection rather than about the configuration.
    /// </remarks>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void CreateDbContext_FallsBackWhenTheEnvironmentNamesNothing(string? supplied)
    {
        using IDisposable environment = ApiTestFixture.OverrideEnvironment(
            new Dictionary<string, string?> { [ConnectionStringVariable] = supplied });

        using DnnDbContext context = new DnnDbContextFactory().CreateDbContext([]);

        context.Database.GetConnectionString().Should().Be(
            DesignTimeFallbackConnectionString,
            "a developer who has exported nothing must get a local database rather than a failure, and must "
            + "certainly not get an empty connection string that fails later for an unrelated-looking reason");
    }

    /// <summary>The fallback carries no credential and names no shared server.</summary>
    /// <remarks>
    /// The fallback string is committed to source control, so anything credential-shaped in it would be a
    /// committed secret. It is also the value used when the operator has said nothing, which is exactly when
    /// it must not be able to reach anything but the developer's own machine.
    /// </remarks>
    [Fact]
    public void TheFallbackConnectionString_CarriesNoCredential()
    {
        using IDisposable environment = ApiTestFixture.OverrideEnvironment(
            new Dictionary<string, string?> { [ConnectionStringVariable] = null });

        using DnnDbContext context = new DnnDbContextFactory().CreateDbContext([]);

        string connectionString = context.Database.GetConnectionString()!;

        connectionString.Should().Contain(
            "(localdb)",
            "the fallback must reach the developer's own machine and nothing else");
        connectionString.Should().Contain(
            "Trusted_Connection=True",
            "integrated authentication is what lets the fallback carry no credential at all");
        connectionString.Should().NotContainEquivalentOf(
            "Password",
            "this string is committed to source control, so a credential in it would be a committed secret");
        connectionString.Should().NotContainEquivalentOf(
            "User Id",
            "a named login would imply a credential is supplied somewhere, which defeats the point of the "
            + "trusted connection above");
    }

    /// <summary>Creating the context opens no connection.</summary>
    /// <remarks>
    /// Construction being inert is what makes it safe for the tooling to build a context against a
    /// production connection string in order to read the model. A connection opened here would hold a
    /// session open for the life of the command, and a query issued here would run against production from a
    /// command whose whole purpose may have been to write a script to a file.
    /// </remarks>
    [Fact]
    public void CreateDbContext_OpensNoConnection()
    {
        using DnnDbContext context = new DnnDbContextFactory().CreateDbContext([]);

        DbConnection connection = context.Database.GetDbConnection();

        connection.State.Should().Be(
            ConnectionState.Closed,
            "the factory documents itself as issuing no query, and an open session held for the life of a "
            + "tooling command is the observable form of breaking that");
    }

    /// <summary>Nothing is contacted, proven by pointing the factory at an address that refuses connections.</summary>
    /// <remarks>
    /// A closed connection state is evidence that nothing is open NOW. This is the stronger statement: the
    /// address is one the operating system has confirmed nothing is listening on, so any attempt to reach it
    /// during construction would surface as a transport failure rather than as a silent success. It also
    /// covers the case a state check cannot - a connection opened and closed again inside the factory.
    /// </remarks>
    [Fact]
    public void CreateDbContext_ContactsNothing_EvenWhenTheAddressRefusesConnections()
    {
        string unreachable = RefusedEndpoint.ConnectionString("DesignTimeUnreachable");

        using IDisposable environment = ApiTestFixture.OverrideEnvironment(
            new Dictionary<string, string?> { [ConnectionStringVariable] = unreachable });

        Func<DnnDbContext> create = () => new DnnDbContextFactory().CreateDbContext([]);

        DnnDbContext context = create.Should().NotThrow(
                "construction must not reach the server: an address nothing listens on would make any "
                + "attempt fail here, and the tooling has to be able to build a context before it can decide "
                + "whether it needs the database at all")
            .Which;

        using (context)
        {
            context.Database.GetConnectionString().Should().Be(unreachable);
            context.Database.GetDbConnection().State.Should().Be(ConnectionState.Closed);
        }
    }

    /// <summary>The context the factory returns exposes the mapped model.</summary>
    /// <remarks>
    /// Reading the model is what the tooling does with the context it is handed, and it does it without a
    /// database. A context that could not build its model offline would fail every migrations command with a
    /// connection error. The expectation comes from the independently derived terminal-schema manifest, so it
    /// tracks the model rather than restating a list.
    /// </remarks>
    [Fact]
    public void CreateDbContext_ExposesTheMappedModelWithoutADatabase()
    {
        string unreachable = RefusedEndpoint.ConnectionString("DesignTimeModelOnly");

        using IDisposable environment = ApiTestFixture.OverrideEnvironment(
            new Dictionary<string, string?> { [ConnectionStringVariable] = unreachable });

        using DnnDbContext context = new DnnDbContextFactory().CreateDbContext([]);

        IReadOnlyList<string> mapped = context.Model.GetEntityTypes()
            .Select(entity => entity.GetTableName())
            .Where(table => !string.IsNullOrEmpty(table))
            .Select(table => table!)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        mapped.Should().BeEquivalentTo(
            TerminalSchema.Tables.Keys,
            "the tooling reads the model from this context offline, so it must be complete without the "
            + "database being reachable");
        context.Database.GetDbConnection().State.Should().Be(
            ConnectionState.Closed,
            "building the model must not have opened anything");
    }

    /// <summary>The arguments the tooling forwards are deliberately ignored.</summary>
    /// <remarks>
    /// The tooling passes whatever followed <c>--</c> on the command line. The factory takes no argument of
    /// its own, and reading one accidentally - a stray value interpreted as a connection string, say - would
    /// repoint the tooling at whatever a developer happened to type.
    /// </remarks>
    [Fact]
    public void CreateDbContext_IgnoresTheArgumentsTheToolingForwards()
    {
        string supplied = RefusedEndpoint.ConnectionString("DesignTimeArguments");

        using IDisposable environment = ApiTestFixture.OverrideEnvironment(
            new Dictionary<string, string?> { [ConnectionStringVariable] = supplied });

        DnnDbContextFactory factory = new();

        using DnnDbContext withoutArguments = factory.CreateDbContext([]);
        using DnnDbContext withArguments = factory.CreateDbContext(
            ["--connection", "Server=somewhere-else;Database=Wrong;", "--verbose"]);

        withArguments.Database.GetConnectionString().Should().Be(
            withoutArguments.Database.GetConnectionString(),
            "a forwarded argument must not be able to repoint the tooling at another catalogue");
        withArguments.Database.GetConnectionString().Should().Be(supplied);
    }

    /// <summary>Each call returns a context of its own.</summary>
    /// <remarks>
    /// The tooling disposes the context it is given. A cached or shared instance would be disposed by the
    /// first command and unusable by the second, which in a single tooling process - <c>dotnet ef</c> runs
    /// several operations in one - would fail the later operation with an object-disposed error naming
    /// nothing useful.
    /// </remarks>
    [Fact]
    public void CreateDbContext_ReturnsAnIndependentContextEachTime()
    {
        DnnDbContextFactory factory = new();

        DnnDbContext first = factory.CreateDbContext([]);
        using DnnDbContext second = factory.CreateDbContext([]);

        second.Should().NotBeSameAs(first, "a shared instance would be disposed out from under a later call");

        first.Dispose();

        Func<string?> readAfterFirstDisposed = () => second.Database.GetConnectionString();

        readAfterFirstDisposed.Should().NotThrow(
            "disposing one context must leave the other usable, which is what independence means here");
    }

    /// <summary>The connection string the factory produced names the database the test asked for.</summary>
    /// <remarks>
    /// A guard against the suite above passing vacuously: every case builds its connection string through
    /// <see cref="RefusedEndpoint"/>, and this confirms that helper produces something the SQL Server client
    /// parses into the catalogue named, so the equality assertions are comparing real connection strings.
    /// </remarks>
    [Fact]
    public void TheProbeConnectionStrings_AreWellFormedForTheSqlServerClient()
    {
        const string catalogue = "DesignTimeWellFormed";

        string supplied = RefusedEndpoint.ConnectionString(catalogue);

        using IDisposable environment = ApiTestFixture.OverrideEnvironment(
            new Dictionary<string, string?> { [ConnectionStringVariable] = supplied });

        using DnnDbContext context = new DnnDbContextFactory().CreateDbContext([]);

        DbConnection connection = context.Database.GetDbConnection();

        connection.Database.Should().Be(
            catalogue,
            "the client parsed the string this suite supplied, so the equality assertions above are "
            + "comparing connection strings the provider actually understood");
        connection.DataSource.Should().Be(
            string.Create(CultureInfo.InvariantCulture, $"{RefusedEndpoint.Host},{RefusedEndpoint.Port}"),
            "and it parsed the address, which is the part that makes 'nothing was contacted' meaningful");
    }
}
