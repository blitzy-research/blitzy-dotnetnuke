using System.Reflection;
using FluentAssertions;
using Xunit;

namespace DnnMigration.IntegrationTests.Infrastructure;

/// <summary>
/// Guards the two NuGet security pins this test project carries, by inspecting the assemblies that were
/// actually copied beside it rather than by reading the project file.
/// </summary>
/// <remarks>
/// THE OUTPUT DIRECTORY IS THE SUBJECT, not the project file and not the lock file. A pin can be present in
/// the project and still lose - a nearer transitive constraint, a central package-management override or a
/// framework-specific asset selection can all change what is finally copied - so the fact asserted here is
/// the one that decides what runs: the file the test host would load.
/// </remarks>
[Trait("Category", "Integration")]
public sealed class SecurityPinTests
{
    /// <summary>SSH.NET is at or above the first release that clears GHSA-q939-rpr3-3284.</summary>
    [Fact]
    public void SshNetIsPinnedAboveTheAdvisoryFloor()
    {
        Version shipped = ShippedVersionOf("Renci.SshNet.dll");

        shipped.Should().BeGreaterThanOrEqualTo(
            new Version(2026, 0, 0),
            "GHSA-q939-rpr3-3284 affects every SSH.NET release below 2026.0.0, and the version that "
            + "arrives transitively through Testcontainers is 2023.0.0");
    }

    /// <summary>The SQLite native bundle is at or above the release that clears GHSA-2m69-gcr7-jv3q.</summary>
    [Fact]
    public void SqliteBundleIsPinnedAboveTheAdvisoryFloor()
    {
        Version shipped = ShippedVersionOf("SQLitePCLRaw.core.dll");

        shipped.Should().BeGreaterThanOrEqualTo(
            new Version(2, 1, 12),
            "GHSA-2m69-gcr7-jv3q covers SQLitePCLRaw.lib.e_sqlite3 through 2.1.11");
    }

    /// <summary>Reads the version of an assembly copied beside this test assembly.</summary>
    /// <param name="fileName">The assembly file name.</param>
    /// <returns>The assembly version recorded in the file's metadata.</returns>
    private static Version ShippedVersionOf(string fileName)
    {
        string directory = Path.GetDirectoryName(typeof(SecurityPinTests).Assembly.Location)!;
        string path = Path.Combine(directory, fileName);

        File.Exists(path).Should().BeTrue(
            "{0} must be copied beside the tests; its absence means the direct PackageReference that pins "
            + "it was removed, which silently restores the vulnerable transitive version",
            fileName);

        return AssemblyName.GetAssemblyName(path).Version!;
    }
}
