using System.Reflection;
using FluentAssertions;
using Xunit;

namespace DnnMigration.IntegrationTests.Infrastructure;

/// <summary>
/// Guards the two NuGet security pins this test project carries, by inspecting the assemblies that were
/// actually copied beside it rather than by reading the project file.
/// </summary>
/// <remarks>
/// <para>
/// DEP-01. Both pins exist for the same reason and both are fragile in the same way: each raises a package
/// that arrives TRANSITIVELY, so the only thing holding the safe version in place is a direct
/// <c>PackageReference</c> that nothing in the code appears to need. That is exactly the kind of reference a
/// later tidy-up deletes - it looks unused, because it is unused - and the transitive vulnerable version
/// would come straight back with no compilation error and no test failure anywhere else.
/// </para>
/// <para>
/// THE OUTPUT DIRECTORY IS THE SUBJECT, not the project file and not the lock file. A pin can be present in
/// the project and still lose - a nearer transitive constraint, a central package-management override or a
/// framework-specific asset selection can all change what is finally copied - so the fact asserted here is
/// the one that decides what runs: the file the test host would load.
/// </para>
/// <para>
/// <c>AssemblyName.GetAssemblyName</c> reads metadata from disk WITHOUT loading the assembly or naming a
/// type from it, which matters: neither package is called by anything in this repository, and a compile-time
/// reference here would make that statement untrue and would drag an unused dependency into the test's own
/// surface. The strings below are the only occurrences of these names in the source tree apart from the
/// notes in the project file.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
public sealed class SecurityPinTests
{
    /// <summary>
    /// SSH.NET is at or above the first release that clears GHSA-q939-rpr3-3284.
    /// </summary>
    /// <remarks>
    /// The advisory's fixed boundary IS 2026.0.0 - every earlier release is affected - so this is a floor
    /// rather than an exact match, and a later release satisfies it. Reached only transitively, through
    /// <c>Testcontainers.MsSql</c> -> <c>Testcontainers</c>, which selects 2023.0.0.
    /// </remarks>
    [Fact]
    public void SshNetIsPinnedAboveTheAdvisoryFloor()
    {
        Version shipped = ShippedVersionOf("Renci.SshNet.dll");

        shipped.Should().BeGreaterThanOrEqualTo(
            new Version(2026, 0, 0),
            "GHSA-q939-rpr3-3284 affects every SSH.NET release below 2026.0.0, and the version that "
            + "arrives transitively through Testcontainers is 2023.0.0");
    }

    /// <summary>
    /// The SQLite native bundle is at or above the release that clears GHSA-2m69-gcr7-jv3q.
    /// </summary>
    /// <remarks>
    /// Asserted alongside the SSH.NET pin because it is the same shape of pin with the same failure mode, and
    /// it had no guard of its own. <c>Microsoft.EntityFrameworkCore.Sqlite 8.0.29</c> selects a vulnerable
    /// 2.1.6, and no patched release exists on that line - the bundle is raised instead so the core, provider
    /// and native pieces move together.
    /// </remarks>
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
    /// <remarks>
    /// A missing file is reported as a missing PIN rather than as a missing file, because that is what its
    /// absence would mean: the reference was removed, so nothing raised the transitive version any more.
    /// </remarks>
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
