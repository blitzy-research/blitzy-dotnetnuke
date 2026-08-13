using DotNet.Testcontainers.Images;
using FluentAssertions;
using Xunit;

namespace DnnMigration.IntegrationTests.Infrastructure;

/// <summary>
/// Guards the supply-chain pin on the SQL Server image this suite provisions when the container route is
/// opted into.
/// </summary>
[Trait("Category", "Integration")]
public sealed class ContainerImagePinTests
{
    /// <summary>The digest separator every pinned reference must carry.</summary>
    private const string DigestPrefix = "@sha256:";

    /// <summary>The reference is pinned by manifest digest, and the digest is a full-length SHA-256.</summary>
    [Fact]
    public void ContainerImageIsDigestPinned()
    {
        TestDatabaseFactory.ContainerImage.Should().Contain(
            DigestPrefix,
            "a tag may be moved by the registry, so the reference must name the manifest itself");

        string digest = TestDatabaseFactory.ContainerImage
            [(TestDatabaseFactory.ContainerImage.IndexOf(DigestPrefix, StringComparison.Ordinal)
                + DigestPrefix.Length)..];

        digest.Should().HaveLength(64, "a SHA-256 manifest digest is 64 hexadecimal characters");
        digest.Should().MatchRegex(
            "^[0-9a-f]{64}$",
            "an abbreviated or upper-case digest is not what a registry will match against");
    }

    /// <summary>
    /// The documentary tag and the pinned digest describe the same repository, so the remark beside the pin
    /// cannot drift into naming a different image from the one that is pulled.
    /// </summary>
    [Fact]
    public void ContainerImageTagDescribesTheSameRepositoryAsTheDigest()
    {
        TestDatabaseFactory.ContainerImage.Should().StartWith(
            "mcr.microsoft.com/mssql/server",
            "the pin must name Microsoft's own SQL Server repository and no mirror of it");

        TestDatabaseFactory.ContainerImageTag.Should().NotBeNullOrWhiteSpace(
            "the readable version is what a failure message and the next version change rely on");
        TestDatabaseFactory.ContainerImageTag.Should().NotContain(
            DigestPrefix,
            "the tag is the readable half; the digest is the other half and lives on the reference");
    }

    /// <summary>
    /// The pinned Testcontainers line can actually PARSE the reference, and round-trips it unchanged.
    /// </summary>
    [Fact]
    public void PinnedTestcontainersParsesTheReferenceUnchanged()
    {
        var image = new DockerImage(TestDatabaseFactory.ContainerImage);

        image.FullName.Should().Be(
            TestDatabaseFactory.ContainerImage,
            "Docker must receive exactly the reference this repository pinned");
    }

    /// <summary>
    /// The conventional combined <c>name:tag@sha256:...</c> form is REFUSED by the pinned Testcontainers
    /// line, which is the whole reason the reference above is digest-only.
    /// </summary>
    [Fact]
    public void CombinedTagAndDigestFormIsRefusedByThePinnedTestcontainersLine()
    {
        string combined =
            $"mcr.microsoft.com/mssql/server:{TestDatabaseFactory.ContainerImageTag}"
            + TestDatabaseFactory.ContainerImage[
                TestDatabaseFactory.ContainerImage.IndexOf(DigestPrefix, StringComparison.Ordinal)..];

        Action parse = () => _ = new DockerImage(combined);

        parse.Should().Throw<ArgumentException>(
            "the pinned Testcontainers line cannot read a combined reference, which is why the pin is "
            + "digest-only rather than the conventional tag-and-digest form");
    }
}
