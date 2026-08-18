using DnnMigration.Infrastructure;
using DnnMigration.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DnnMigration.IntegrationTests.Infrastructure;

/// <summary>
/// Verifies that the infrastructure registration refuses a database connection string that cannot work, at
/// the moment the host is built rather than on the first request.
/// </summary>
/// <remarks>
/// Every fact below also asserts that the refusal reveals NOTHING about the value, because a connection
/// string carries a credential and a start-up exception is written to the log.
/// </remarks>
[Trait("Category", "Integration")]
public class ConnectionStringValidationTests
{
    private const string UsableConnectionString =
        "Server=db.example.invalid,1433;Database=DotNetNuke;User Id=dnn_app;Password=Sfx7!qLp2vRz;Encrypt=True";

    /// <summary>A complete, placeholder-free connection string is accepted.</summary>
    [Fact]
    public void AUsableConnectionStringIsAccepted()
    {
        Register(UsableConnectionString).Should().NotThrow();
    }

    /// <summary>Integrated security and an access token each satisfy the authentication rule.</summary>
    /// <param name="connectionString">The connection string under test.</param>
    /// <remarks>
    /// The breadth is deliberate: refusing a legitimately credential-free connection string would stop a
    /// correctly configured deployment, which is worse than the failure the rule exists to catch.
    /// </remarks>
    [Theory]
    [InlineData("Server=db.example.invalid;Database=DotNetNuke;Integrated Security=true")]
    [InlineData("Server=db.example.invalid;Database=DotNetNuke;Authentication=Active Directory Managed Identity")]
    public void ACredentialFreeAuthenticationMechanismIsAccepted(string connectionString)
    {
        Register(connectionString).Should().NotThrow();
    }

    /// <summary>An absent or blank value is refused and the key is named in both spellings.</summary>
    /// <param name="connectionString">The configured value.</param>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AnAbsentConnectionStringIsRefused(string? connectionString)
    {
        Register(connectionString).Should().Throw<InvalidOperationException>()
            .WithMessage("*ConnectionStrings:Default*")
            .And.Message.Should().Contain(
                "ConnectionStrings__Default",
                "an operator configuring a container needs the environment spelling too");
    }

    /// <summary>
    /// A connection string still carrying a documented template placeholder is refused, whichever value it
    /// sits in.
    /// </summary>
    /// <param name="connectionString">The configured value.</param>
    [Theory]
    [InlineData("Server=host.docker.internal,1433;Database=DotNetNuke;User Id=CHANGE_ME;Password=CHANGE_ME;Encrypt=True")]
    [InlineData("Server=host.docker.internal,1433;Database=DotNetNuke;User Id=dnn_app;Password=change-me-before-deploying;Encrypt=True")]
    [InlineData("Server=your-server.example,1433;Database=DotNetNuke;User Id=dnn_app;Password=Sfx7!qLp2vRz")]
    [InlineData("Server=db.example.invalid;Database=DotNetNuke;User Id=dnn_app;Password=TODO")]
    public void APlaceholderConnectionStringIsRefused(string connectionString)
    {
        Register(connectionString).Should().Throw<InvalidOperationException>()
            .WithMessage("*placeholder*");
    }

    /// <summary>A structurally incomplete connection string is refused, naming the missing part.</summary>
    /// <param name="connectionString">The configured value.</param>
    /// <param name="expectedFragment">The wording the refusal must carry.</param>
    /// <remarks>
    /// Each case would otherwise start a host that fails every database-backed request: a connection string
    /// missing the server or the database can never reach the existing DotNetNuke schema, and a user id
    /// with no password is the shape a half-edited template leaves behind.
    /// </remarks>
    [Theory]
    [InlineData("Database=DotNetNuke;User Id=dnn_app;Password=Sfx7!qLp2vRz", "names no server")]
    [InlineData("Server=db.example.invalid;User Id=dnn_app;Password=Sfx7!qLp2vRz", "names no database")]
    [InlineData("Server=db.example.invalid;Database=DotNetNuke", "names no way to authenticate")]
    [InlineData("Server=db.example.invalid;Database=DotNetNuke;User Id=dnn_app", "names no way to authenticate")]
    public void AnIncompleteConnectionStringIsRefused(string connectionString, string expectedFragment)
    {
        Register(connectionString).Should().Throw<InvalidOperationException>()
            .WithMessage("*" + expectedFragment + "*");
    }

    /// <summary>A value that is not a connection string at all is refused without echoing it.</summary>
    /// <remarks>
    /// The parser's own exception quotes the fragment it could not read, which for a connection string may
    /// be the credential, so it is replaced rather than wrapped - and this fact is what stops it being
    /// "helpfully" chained back in later.
    /// </remarks>
    [Fact]
    public void AMalformedConnectionStringIsRefusedWithoutEchoingIt()
    {
        const string Malformed = "Server=db.example.invalid;Database=DotNetNuke;Password=Sfx7!qLp2vRz;=nonsense";

        Exception thrown = Register(Malformed).Should().Throw<InvalidOperationException>().Which;

        thrown.Message.Should().Contain("not a valid SQL Server connection string");
        thrown.Message.Should().NotContain("Sfx7!qLp2vRz");
        thrown.InnerException.Should().BeNull(
            "the parser's message quotes the fragment it could not read, which may be the credential");
    }

    /// <summary>No refusal reproduces the server, the database, the login or the password.</summary>
    /// <param name="connectionString">A refused value.</param>
    [Theory]
    [InlineData("Server=secret-host.internal,1433;Database=SecretCatalog;User Id=SecretLogin;Password=CHANGE_ME")]
    [InlineData("Database=SecretCatalog;User Id=SecretLogin;Password=Sfx7!qLp2vRz")]
    [InlineData("Server=secret-host.internal;Database=SecretCatalog")]
    public void ARefusalNeverEchoesAnyPartOfTheValue(string connectionString)
    {
        string message = Register(connectionString).Should().Throw<InvalidOperationException>().Which.Message;

        message.Should().NotContainAny(
            "secret-host.internal",
            "SecretCatalog",
            "SecretLogin",
            "Sfx7!qLp2vRz");
    }

    // ---- The connection-attempt bound ------------------------------------------------------------------
    //
    // A QA run measured a database-outage 503 taking roughly fifteen seconds to be produced. The answer was
    // correct - 503 with Retry-After - but SqlClient's default connection attempt is fifteen seconds, so every
    // caller waited out that default before receiving it, and a health probe reported an outage a quarter of a
    // minute after it began.

    /// <summary>A connection string that states no timeout is given a bounded one.</summary>
    /// <remarks>
    /// Asserted through the string the context is actually registered with, rather than by reaching into the
    /// private method, so this measures the value a connection attempt would really use.
    /// </remarks>
    [Fact]
    public void AConnectionStringStatingNoTimeout_IsGivenABoundedOne()
    {
        SqlConnectionStringBuilder registered = RegisteredConnection(UsableConnectionString);

        registered.ConnectTimeout.Should().BeGreaterThan(
            0,
            "a zero timeout means wait forever, which is the opposite of the intent");
        registered.ConnectTimeout.Should().BeLessThan(
            15,
            "the whole point is to answer sooner than SqlClient's own default");
        registered.ShouldSerialize("Connect Timeout").Should().BeTrue(
            "the value has to be written into the string to have any effect");
    }

    /// <summary>
    /// ⚠ AN OPERATOR'S OWN TIMEOUT IS NEVER OVERRIDDEN, in any of the three spellings it can be written in.
    /// </summary>
    /// <param name="keyword">The synonym the deployment used.</param>
    /// <remarks>
    /// Detecting "the operator said nothing" is subtler than it looks, and this theory is what pins the
    /// mechanism. <c>ConnectTimeout</c> reads 15 whether the keyword was supplied AS 15 or omitted entirely, and
    /// <c>ContainsKey</c> answers true in both cases because the builder pre-populates every keyword it knows -
    /// so either of those tests would silently overwrite a deployment that had tuned this. The 30 below is
    /// deliberately LONGER than SqlClient's default, so a rule that merely clamped high values would fail here.
    /// </remarks>
    [Theory]
    [InlineData("Connect Timeout")]
    [InlineData("Connection Timeout")]
    [InlineData("Timeout")]
    public void AnOperatorsOwnTimeout_IsPreserved(string keyword)
    {
        SqlConnectionStringBuilder registered =
            RegisteredConnection($"{UsableConnectionString};{keyword}=30");

        registered.ConnectTimeout.Should().Be(
            30,
            "a deployment that has tuned this keeps its value, whichever synonym it used");
    }

    /// <summary>A deployment that deliberately asks to wait longer is respected too.</summary>
    /// <remarks>
    /// The companion to the case above, and the reason the rule is "fill in what is missing" rather than "cap
    /// what is present". A cross-region or heavily loaded server may legitimately need longer than the default,
    /// and a bound imposed against the operator's stated wish would break exactly that deployment.
    /// </remarks>
    [Fact]
    public void ADeliberatelyLongTimeout_IsNotClamped()
    {
        RegisteredConnection($"{UsableConnectionString};Connect Timeout=120")
            .ConnectTimeout.Should().Be(120);
    }

    /// <summary>Adding a timeout does not disturb anything else the connection string states.</summary>
    /// <remarks>
    /// The string is rebuilt through <see cref="SqlConnectionStringBuilder"/> when a timeout is added, so this
    /// case exists to prove the rebuild is lossless. Losing the encryption setting or the catalogue here would
    /// be a far worse fault than the one being fixed, and it would surface only at a live connection.
    /// </remarks>
    [Fact]
    public void AddingATimeout_PreservesEveryOtherSetting()
    {
        SqlConnectionStringBuilder registered = RegisteredConnection(UsableConnectionString);
        var original = new SqlConnectionStringBuilder(UsableConnectionString);

        registered.DataSource.Should().Be(original.DataSource);
        registered.InitialCatalog.Should().Be(original.InitialCatalog);
        registered.UserID.Should().Be(original.UserID);
        registered.Password.Should().Be(original.Password);
        registered.Encrypt.Should().Be(original.Encrypt);
    }

    /// <summary>
    /// Registers the infrastructure and returns the connection string the context was configured with.
    /// </summary>
    /// <param name="connectionString">The configured value.</param>
    /// <returns>The connection string the registered context resolves to, parsed.</returns>
    /// <remarks>
    /// Read back from the composed <see cref="DbContextOptions"/> rather than from configuration, because the
    /// question is what the CONTEXT will connect with - which is the value after registration has had its say.
    /// </remarks>
    private static SqlConnectionStringBuilder RegisteredConnection(string connectionString)
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["ConnectionStrings:Default"] = connectionString,
            })
            .Build();

        ServiceProvider provider = new ServiceCollection()
            .AddInfrastructure(configuration)
            .BuildServiceProvider();

        using IServiceScope scope = provider.CreateScope();

        DbContextOptions<DnnDbContext> options = scope.ServiceProvider
            .GetRequiredService<DbContextOptions<DnnDbContext>>();

        RelationalOptionsExtension? relational = options.Extensions
            .OfType<RelationalOptionsExtension>()
            .FirstOrDefault();

        relational.Should().NotBeNull("the context is registered against a relational provider");
        relational!.ConnectionString.Should().NotBeNullOrWhiteSpace();

        return new SqlConnectionStringBuilder(relational.ConnectionString);
    }

    private static Action Register(string? connectionString)
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["ConnectionStrings:Default"] = connectionString,
            })
            .Build();

        return () => new ServiceCollection().AddInfrastructure(configuration);
    }
}
