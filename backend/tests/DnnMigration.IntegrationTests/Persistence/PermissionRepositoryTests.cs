using DnnMigration.Domain.Abstractions.Repositories;
using DnnMigration.Domain.Entities;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DnnMigration.IntegrationTests.Persistence;

/// <summary>
/// Verifies that permission-catalogue reads honour the resource identifiers supplied to the repository.
/// </summary>
/// <remarks>
/// SEC-033: the legacy page-catalogue procedure ignored its page argument. The migrated repository keeps
/// the shared <c>SYSTEM_TAB</c> catalogue for every existing page, but an arbitrary identifier must no
/// longer disclose that installation-wide metadata.
/// </remarks>
[Trait("Category", "Integration")]
[Collection(IntegrationTestCollection.Name)]
public sealed class PermissionRepositoryTests
{
    private const int UnknownTabId = 987_654_321;

    private readonly ApiTestFixture _fixture;

    /// <summary>Initialises a new instance of the <see cref="PermissionRepositoryTests"/> class.</summary>
    /// <param name="fixture">The shared database and seed.</param>
    public PermissionRepositoryTests(ApiTestFixture fixture) => _fixture = fixture;

    /// <summary>An identifier naming no page returns no catalogue definitions.</summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task GetByTabIdAsync_WhenThePageDoesNotExist_ReturnsNoMetadata()
    {
        using IServiceScope scope = _fixture.Services.CreateScope();
        IPermissionRepository permissions =
            scope.ServiceProvider.GetRequiredService<IPermissionRepository>();

        IReadOnlyList<Permission> definitions = await permissions
            .GetByTabIdAsync(UnknownTabId, CancellationToken.None);

        definitions.Should().BeEmpty();
    }

    /// <summary>An existing page receives the shared page-scope catalogue.</summary>
    /// <returns>A task representing the assertion.</returns>
    [Fact]
    public async Task GetByTabIdAsync_WhenThePageExists_ReturnsThePageScope()
    {
        using IServiceScope scope = _fixture.Services.CreateScope();
        IPermissionRepository permissions =
            scope.ServiceProvider.GetRequiredService<IPermissionRepository>();

        IReadOnlyList<Permission> definitions = await permissions
            .GetByTabIdAsync(_fixture.Seed.RootTabId, CancellationToken.None);

        definitions.Should().NotBeEmpty();
        definitions.Should().OnlyContain(
            definition => definition.PermissionCode == IntegrationSeed.TabPermissionCode);
        definitions.Select(definition => definition.PermissionId).Should()
            .Contain(_fixture.Seed.TabViewPermissionId)
            .And.Contain(_fixture.Seed.TabEditPermissionId);
    }
}
