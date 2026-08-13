using DnnMigration.Application.Options;
using FluentAssertions;
using Xunit;

namespace DnnMigration.UnitTests.Validation;

/// <summary>
/// Pins the rule that keeps the durable refresh-token store out of the DotNetNuke catalogue.
/// </summary>
/// <remarks>
/// <para>
/// <strong>WHY THE RULE WAS REPLACED RATHER THAN EXTENDED.</strong> MIGRATION: SEC-07. It used to refuse the
/// configuration only when the two connection strings named the same SERVER and the same CATALOGUE, compared as
/// raw text, and both halves of that leaked.
/// </para>
/// <para>
/// A host is spellable as <c>localhost</c>, <c>127.0.0.1</c>, <c>(local)</c>, <c>.</c>,
/// <c>tcp:localhost,1433</c>, the machine's own name or a named instance - every pair of those is the same
/// server while comparing unequal - so the guard was defeated by writing the host differently on one side of
/// the configuration. And a connection string that omitted <c>Database</c> read as "names no catalogue" and
/// returned false outright, which waved through the single most dangerous configuration there is: a login
/// whose DEFAULT catalogue is the DotNetNuke database. AAP rule T4 freezes that schema, and this is the check
/// that stands between it and a session table.
/// </para>
/// <para>
/// The rule that replaced it asks a question that is decidable from the text: name the catalogue explicitly, do
/// not name a system catalogue, and do not name the application's. Canonicalising host spellings is a problem
/// with no end and no way to know it is finished; comparing catalogue names is one ordinal comparison and
/// cannot be spelled around.
/// </para>
/// <para>
/// These are unit tests because the rule is a pure function of two strings. They are the tests that would have
/// failed against the previous rule, which is what makes them worth writing.
/// </para>
/// </remarks>
public sealed class RefreshTokenStoreCatalogueIsolationTests
{
    /// <summary>The application's own catalogue, as the deployment templates name it.</summary>
    private const string ApplicationConnection =
        "Server=host.docker.internal,1433;Database=DotNetNuke;User Id=api;Password=secret;Encrypt=True";

    /// <summary>
    /// A session catalogue whose name matches the application's is refused however differently its host is
    /// spelled.
    /// </summary>
    /// <param name="sessionConnection">The session connection string to judge.</param>
    /// <remarks>
    /// Every row here PASSED the previous rule. The first names the same host in a different notation, the
    /// second uses the loopback address, the third the legacy local alias, the fourth the shorthand for it, the
    /// fifth a transport prefix and a port, and the sixth the alternative keyword for the catalogue itself.
    /// Each addresses a database that may well be the DotNetNuke one, and a textual server comparison called
    /// every one of them a different server.
    /// </remarks>
    [Theory]
    [InlineData("Server=HOST.DOCKER.INTERNAL;Database=DotNetNuke;User Id=api;Password=secret")]
    [InlineData("Server=127.0.0.1,1433;Database=DotNetNuke;User Id=api;Password=secret")]
    [InlineData("Server=(local);Database=DotNetNuke;User Id=api;Password=secret")]
    [InlineData("Server=.;Database=DotNetNuke;User Id=api;Password=secret")]
    [InlineData("Data Source=tcp:localhost,1433;Initial Catalog=DotNetNuke;User Id=api;Password=secret")]
    [InlineData("Server=sql;Initial Catalog=dotnetnuke;User Id=api;Password=secret")]
    public void ASessionCatalogueNamedLikeTheApplications_IsRefusedHoweverTheHostIsSpelled(
        string sessionConnection)
    {
        Settings(sessionConnection).Validate(ApplicationConnection).Should().ContainMatch(
            "*must name a catalogue other than the DotNetNuke database*",
            "the catalogue NAME decides this, because a host has too many equivalent spellings for a "
            + "comparison of hosts to be sound");
    }

    /// <summary>
    /// A session connection string that names no catalogue at all is refused rather than treated as isolated.
    /// </summary>
    /// <param name="sessionConnection">The session connection string to judge.</param>
    /// <remarks>
    /// THE BYPASS THAT MATTERED MOST. An absent catalogue used to make the comparison return false, so the
    /// configuration was accepted - and a connection string with no catalogue resolves to the login's default,
    /// which for a login created for this application is routinely the DotNetNuke database itself. The rule
    /// could not have been weaker in the case where it mattered more.
    /// </remarks>
    [Theory]
    [InlineData("Server=sql,1433;User Id=api;Password=secret;Encrypt=True")]
    [InlineData("Server=sql,1433;User Id=api;Password=secret;Database=")]
    public void ASessionConnectionNamingNoCatalogue_IsRefused(string sessionConnection)
    {
        Settings(sessionConnection).Validate(ApplicationConnection).Should().ContainMatch(
            "*must name the session catalogue explicitly*",
            "a catalogue that is not named cannot be shown to differ from the application's, and the default "
            + "it resolves to is routinely the application's own");
    }

    /// <summary>A session catalogue that is one of the server's own administrative databases is refused.</summary>
    /// <param name="catalogue">The catalogue named.</param>
    /// <remarks>
    /// <c>tempdb</c> earns its place twice: a table created there does not survive a restart, so a deployment
    /// naming it would run process-local sessions under a durable provider's name and believe otherwise - the
    /// exact confusion the provider setting exists to prevent.
    /// </remarks>
    [Theory]
    [InlineData("master")]
    [InlineData("MODEL")]
    [InlineData("msdb")]
    [InlineData("TempDb")]
    public void ASystemCatalogue_IsRefused(string catalogue)
    {
        string connection = FormattableString.Invariant(
            $"Server=sql,1433;Database={catalogue};User Id=api;Password=secret");

        Settings(connection).Validate(ApplicationConnection).Should().ContainMatch(
            "*system catalogue*",
            "session state belongs in a catalogue provisioned for it, never in a server's own administrative "
            + "databases");
    }

    /// <summary>
    /// An application connection string that names no catalogue is refused, because the isolation cannot be
    /// established against it.
    /// </summary>
    /// <remarks>
    /// Fail-closed rather than fail-silent. The alternative - accepting the session catalogue because the other
    /// side of the comparison is unknown - is how the previous rule behaved, and it is the branch that made the
    /// rule bypassable.
    /// </remarks>
    [Fact]
    public void AnApplicationConnectionNamingNoCatalogue_IsRefused()
    {
        Settings("Server=sql,1433;Database=DnnSessions;User Id=api;Password=secret")
            .Validate("Server=sql,1433;User Id=api;Password=secret")
            .Should().ContainMatch(
                "*ConnectionStrings:Default must name its catalogue explicitly*",
                "the rule is that the two catalogues differ, and an unnamed catalogue makes that impossible "
                + "to establish rather than trivially true");
    }

    /// <summary>A distinctly named session catalogue on the same server is accepted.</summary>
    /// <remarks>
    /// The positive case, and it is what makes the refusals above evidence of a rule rather than of a blanket
    /// veto. Sharing one server is the ordinary deployment - a small catalogue beside the application's - and
    /// nothing about it breaches rule T4, because no object of this migration's is created in the frozen
    /// schema.
    /// </remarks>
    [Fact]
    public void ADistinctlyNamedSessionCatalogueOnTheSameServer_IsAccepted()
    {
        Settings("Server=host.docker.internal,1433;Database=DnnMigrationSessions;User Id=api;Password=secret;Encrypt=True")
            .Validate(ApplicationConnection)
            .Should().BeEmpty();
    }

    /// <summary>The isolation rules apply only to the durable provider.</summary>
    /// <remarks>
    /// A deployment running the process-local store configures no session catalogue at all, and must not be
    /// failed for it. Asserted so that the stricter rules above cannot leak onto the default configuration,
    /// which would break every deployment that had never opted into the durable store.
    /// </remarks>
    [Fact]
    public void TheProcessLocalProviderIsUnaffectedByTheCatalogueRules()
    {
        new RefreshTokenStoreOptions { Provider = RefreshTokenStoreOptions.InProcessProvider }
            .Validate(ApplicationConnection)
            .Should().BeEmpty();
    }

    /// <summary>The setting that authorised runtime table creation is no longer part of this type.</summary>
    /// <remarks>
    /// MIGRATION: SEC-05. Its name survives as a constant only so the host can REFUSE a configuration that
    /// still sets it - binding ignores unknown keys, so a deployment relying on runtime creation would
    /// otherwise have had its setting silently disregarded and learned of the change from a failed sign-in.
    /// This pins that the property itself is gone, which is the fact that makes the store's probe-only
    /// behaviour structural rather than conditional.
    /// </remarks>
    [Fact]
    public void TheRuntimeTableCreationSettingIsGoneAndOnlyItsNameRemains()
    {
        typeof(RefreshTokenStoreOptions)
            .GetProperty(RefreshTokenStoreOptions.RemovedCreateTableSetting)
            .Should().BeNull(
                "the store provisions nothing, so there is no setting that could authorise it to");

        RefreshTokenStoreOptions.RemovedCreateTableSetting.Should().Be("CreateTableIfMissing");
    }

    /// <summary>Builds durable-provider settings addressing one session catalogue.</summary>
    /// <param name="connectionString">The session connection string.</param>
    /// <returns>The settings.</returns>
    private static RefreshTokenStoreOptions Settings(string connectionString) => new()
    {
        Provider = RefreshTokenStoreOptions.SqlServerProvider,
        ConnectionString = connectionString,
    };
}
