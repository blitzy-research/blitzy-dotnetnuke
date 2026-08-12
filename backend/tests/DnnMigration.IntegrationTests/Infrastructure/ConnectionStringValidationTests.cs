using DnnMigration.Infrastructure;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DnnMigration.IntegrationTests.Infrastructure;

/// <summary>
/// Verifies that the infrastructure registration refuses a database connection string that cannot work,
/// at the moment the host is built rather than on the first request.
/// </summary>
/// <remarks>
/// <para>
/// MIGRATION: the registration used to accept any non-blank value. That let the documented deployment
/// template through - <c>docker/.env.example</c> shipped an active <c>DB_CONNECTION_STRING</c> whose user
/// id and password were both <c>CHANGE_ME</c> - so the process started, the <c>/health</c> liveness probe
/// answered 200 because it deliberately excludes the database, and compose released the frontend behind an
/// API on which every database-backed request and every sign-in failed. The signing key already had an
/// equivalent guard; the connection string did not, and the asymmetry was the defect.
/// </para>
/// <para>
/// Every fact below also asserts that the refusal reveals NOTHING about the value, because a connection
/// string carries a credential and a start-up exception is written to the log.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
public class ConnectionStringValidationTests
{
    private const string UsableConnectionString =
        "Server=db.example.invalid,1433;Database=DotNetNuke;User Id=dnn_app;Password=Sfx7!qLp2vRz;Encrypt=True";

    /// <summary>A complete, placeholder-free connection string is accepted.</summary>
    /// <remarks>
    /// The positive control. Without it every fact below could pass because the registration refuses
    /// everything, which would be a different defect wearing the same green tick.
    /// </remarks>
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
    /// <remarks>
    /// The first case is the exact shape the deployment template used to ship. Matching is by containment
    /// and case-insensitive, so the padded variants are refused too - padding a placeholder into something
    /// that looks like a real value is the most likely way one reaches production.
    /// </remarks>
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
    /// Each case would otherwise start a host that fails every database-backed request: a connection
    /// string missing the server or the database can never reach the existing DotNetNuke schema, and a
    /// user id with no password is the shape a half-edited template leaves behind.
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
    /// <remarks>
    /// Asserted across every refusal class rather than once, because a message added later to one branch
    /// would otherwise be the only place the rule is not checked.
    /// </remarks>
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
