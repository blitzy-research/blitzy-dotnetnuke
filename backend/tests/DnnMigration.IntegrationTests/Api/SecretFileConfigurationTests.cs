using DnnMigration.Api.Extensions;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace DnnMigration.IntegrationTests.Api;

/// <summary>
/// Verifies that a secret delivered as a FILE binds to the same configuration key as its environment
/// spelling, that a mounted value outranks an inherited environment variable, and that a host which mounts
/// nothing is unaffected.
/// </summary>
/// <remarks>
/// <para>
/// <c>docker/.env.example</c> told operators that Docker Swarm, Kubernetes or a managed secret store could
/// deliver the connection string and the signing key as mounted files "without any code change", and no
/// key-per-file configuration source was registered anywhere - so a <c>/run/secrets</c> mount was simply
/// not read. The template described a capability the host did not have.
/// </para>
/// <para>
/// The provider is exercised directly rather than through the container's own start-up, because the
/// property under test belongs to configuration composition: which key a file name becomes, and which
/// source wins when two supply the same key. The composition root's registration - the directory it names,
/// its optionality and its position last in the chain - is asserted in the same shape it is written there.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
public sealed class SecretFileConfigurationTests : IDisposable
{
    /// <summary>The throwaway directory standing in for a secret mount.</summary>
    private readonly string _secretsDirectory =
        Path.Combine(Path.GetTempPath(), "dnn-secret-file-tests-" + Guid.NewGuid().ToString("N"));

    /// <summary>Initialises a new instance of the <see cref="SecretFileConfigurationTests"/> class.</summary>
    public SecretFileConfigurationTests() => Directory.CreateDirectory(_secretsDirectory);

    /// <summary>A file name becomes the configuration key its environment spelling would.</summary>
    /// <param name="fileName">The secret's file name, as an orchestrator mounts it.</param>
    /// <param name="expectedKey">The configuration key the application reads.</param>
    /// <remarks>
    /// The double underscore is the whole point: it is the section separator in an environment variable
    /// name, and the provider translates it identically, which is what makes "the same value, delivered
    /// differently" true. The two keys exercised are exactly the two this API requires from a secret store.
    /// </remarks>
    [Theory]
    [InlineData("Jwt__Secret", "Jwt:Secret")]
    [InlineData("ConnectionStrings__Default", "ConnectionStrings:Default")]
    [InlineData("LegacyCredentials__DecryptionKey", "LegacyCredentials:DecryptionKey")]
    public void AMountedFileNameBecomesTheConfigurationKey(string fileName, string expectedKey)
    {
        const string value = "a-value-delivered-as-a-file";
        File.WriteAllText(Path.Combine(_secretsDirectory, fileName), value);

        IConfiguration configuration = new ConfigurationBuilder()
            .AddKeyPerFile(_secretsDirectory, optional: true)
            .Build();

        configuration[expectedKey].Should().Be(
            value,
            "a file-delivered secret has to reach the same key as the environment variable it replaces, or "
            + "the documented migration off environment delivery would not work");
    }

    /// <summary>A trailing newline in the file does not become part of the secret.</summary>
    [Fact]
    public void ATrailingNewlineIsNotPartOfTheSecret()
    {
        const string value = "a-signing-key-of-at-least-thirty-two-bytes";
        File.WriteAllText(Path.Combine(_secretsDirectory, "Jwt__Secret"), value + Environment.NewLine);

        IConfiguration configuration = new ConfigurationBuilder()
            .AddKeyPerFile(_secretsDirectory, optional: true)
            .Build();

        configuration["Jwt:Secret"].Should().Be(value);
    }

    /// <summary>A mounted secret outranks an environment variable supplying the same key.</summary>
    /// <remarks>
    /// The direction is the deliberate one. A deployment mounts a secret precisely to stop the value
    /// appearing in an environment block that <c>docker inspect</c> can read, so an inherited variable of
    /// the same name must not silently defeat it - and the migration off environment delivery is then one
    /// step (mount the file) rather than a cutover.
    /// </remarks>
    [Fact]
    public void AMountedSecretOutranksAnEnvironmentVariable()
    {
        File.WriteAllText(Path.Combine(_secretsDirectory, "Jwt__Secret"), "from-the-mounted-file");

        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["Jwt:Secret"] = "from-the-environment",
            })
            .AddKeyPerFile(_secretsDirectory, optional: true)
            .Build();

        configuration["Jwt:Secret"].Should().Be("from-the-mounted-file");
    }

    /// <summary>A host that mounts nothing is unaffected, and does not fail to start.</summary>
    /// <remarks>
    /// The same image runs in the base compose topology, in this test host and on a workstation, none of
    /// which mounts a secret directory. Optionality is therefore load-bearing: without it the absent
    /// directory would stop every one of them.
    /// </remarks>
    [Fact]
    public void AnAbsentSecretsDirectoryContributesNothingAndDoesNotThrow()
    {
        string absent = Path.Combine(_secretsDirectory, "not-created");

        Action build = () => new ConfigurationBuilder()
            .AddKeyPerFile(absent, optional: true)
            .Build();

        build.Should().NotThrow();

        IConfiguration configuration = new ConfigurationBuilder()
            .AddKeyPerFile(absent, optional: true)
            .Build();

        configuration["Jwt:Secret"].Should().BeNull();
    }

    /// <summary>The published contract for the mount point is the documented one.</summary>
    [Fact]
    public void TheMountPointContractIsPublishedAsDocumented()
    {
        ServiceCollectionExtensions.DefaultSecretsDirectory.Should().Be("/run/secrets");
        ServiceCollectionExtensions.SecretsDirectorySectionName.Should().Be("Secrets:Directory");
    }

    /// <summary>Removes the throwaway directory.</summary>
    public void Dispose()
    {
        if (Directory.Exists(_secretsDirectory))
        {
            Directory.Delete(_secretsDirectory, recursive: true);
        }
    }
}
