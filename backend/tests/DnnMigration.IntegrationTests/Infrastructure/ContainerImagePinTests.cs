using DotNet.Testcontainers.Images;
using FluentAssertions;
using Xunit;

namespace DnnMigration.IntegrationTests.Infrastructure;

/// <summary>
/// Guards the supply-chain pin on the SQL Server image this suite provisions when the container route is
/// opted into.
/// </summary>
/// <remarks>
/// <para>
/// DEP-04. The reference used to name a cumulative-update tag and record its digest only in a remark, so
/// nothing enforced the match. A cumulative-update tag is immutable BY CONVENTION, not by construction: a
/// registry may move it, and a re-pushed or compromised tag would then be pulled and trusted without a
/// single line of this repository changing. These facts make the pin structural.
/// </para>
/// <para>
/// They are unit-shaped on purpose - no Docker daemon, no network, no container - because what they protect
/// is the REFERENCE, and a fact that needed a daemon would be skipped in exactly the environments where a
/// silent regression matters most.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
public sealed class ContainerImagePinTests
{
    /// <summary>The digest separator every pinned reference must carry.</summary>
    private const string DigestPrefix = "@sha256:";

    /// <summary>
    /// The reference is pinned by manifest digest, and the digest is a full-length SHA-256.
    /// </summary>
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
    /// <remarks>
    /// This is the fact that makes the pin real rather than merely well-intentioned. <c>WithImage(string)</c>
    /// hands the reference to this parser, so a reference this type mangles or rejects is a reference the
    /// container route cannot use - and the failure would appear only when somebody opted into the container
    /// route, which is not where anybody is looking.
    /// </remarks>
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
    /// <remarks>
    /// <para>
    /// ⚠ THIS FACT EXISTS TO STOP A WELL-MEANING CORRECTION. The combined form is what a reviewer, a
    /// linter or a hardening guide will suggest, and it reads better - but <c>DockerImage</c> on this line
    /// has no digest concept at all and raises on it, so adopting it would break the container route while
    /// looking like an improvement. Upgrading the library to gain support is not open either: AAP 0.6.4
    /// rejects <c>Testcontainers.MsSql 4.13.0</c>.
    /// </para>
    /// <para>
    /// If this fact ever FAILS, the library has gained digest support - and then the combined form becomes
    /// the better reference and both this fact and the pin should change together.
    /// </para>
    /// </remarks>
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
