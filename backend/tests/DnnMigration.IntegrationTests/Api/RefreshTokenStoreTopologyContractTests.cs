using System.Net;
using DnnMigration.Application.Options;
using DnnMigration.IntegrationTests;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace DnnMigration.IntegrationTests.Api;

/// <summary>
/// Verifies that the composition root actually performs the refresh-token store topology check, so a
/// deployment whose declared store is not the store it runs never serves a request.
/// </summary>
/// <remarks>
/// <para>
/// WHY THIS SUITE IS SEPARATE FROM THE UNIT FACTS. A unit suite already proves the check itself - what it
/// accepts, what it refuses, and that a store registered after <c>AddInfrastructure</c> replaces the shipped
/// one. What no unit fact can prove is that anything CALLS it. The check is an explicit statement in
/// <c>Program.cs</c> rather than a hosted service or an options validator, for reasons argued at its
/// declaration, and an explicit call is exactly the kind of thing a later edit removes without any test
/// noticing. These facts build the real host and depend on its refusal.
/// </para>
/// <para>
/// WHAT THE CHECK PROTECTS. This solution's refresh-token store keeps its state in the API process, because
/// AAP rule T4 forbids adding a table to the existing DotNetNuke schema, AAP 0.6 freezes a dependency
/// inventory with no distributed-cache client, and AAP 0.9.3 reproduces a two-service container topology
/// verbatim. A deployment needing cross-process refresh continuity therefore registers its own
/// <c>IRefreshTokenStore</c> and declares <c>RefreshTokenStore:Provider=External</c>. The failure worth
/// preventing is the half-done version of that: the declaration made, the registration forgotten, and the
/// deployment scaled out in the belief that refresh state is shared.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
[Collection(IntegrationTestCollection.Name)]
public sealed class RefreshTokenStoreTopologyContractTests
{
    /// <summary>Environment spelling of the provider setting, as compose would supply it.</summary>
    private const string ProviderVariable = "RefreshTokenStore__Provider";

    /// <summary>Environment spelling of the capacity setting.</summary>
    private const string CapacityVariable = "RefreshTokenStore__MaximumTrackedTokens";

    /// <summary>The liveness view, which is what the image's own health check reads.</summary>
    private const string HealthEndpoint = "/health";

    private readonly ApiTestFixture _fixture;

    /// <summary>
    /// Initialises a new instance of the <see cref="RefreshTokenStoreTopologyContractTests"/> class.
    /// </summary>
    /// <param name="fixture">The shared host and database.</param>
    public RefreshTokenStoreTopologyContractTests(ApiTestFixture fixture) => _fixture = fixture;

    /// <summary>
    /// Declaring the shipped store explicitly starts and serves, which is the control for the refusals below.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    /// <remarks>
    /// Without it, every refusal below could be produced by the harness rather than by the check, and the
    /// suite would pass while asserting nothing about the setting.
    /// </remarks>
    [Fact]
    public async Task DeclaringTheShippedStoreStartsAndServes()
    {
        using (ApiTestFixture.OverrideEnvironment(
            Overrides((ProviderVariable, RefreshTokenStoreOptions.InProcessProvider))))
        {
            await using var host = new TopologyHost();
            using HttpClient client = host.CreateClient();

            using HttpResponseMessage response = await client.GetAsync(HealthEndpoint);

            response.StatusCode.Should().Be(
                HttpStatusCode.OK,
                "the declared store is the store the container resolves, so the topology is valid");
        }
    }

    /// <summary>
    /// Declaring a deployment-supplied store without registering one stops the host before it serves.
    /// </summary>
    /// <remarks>
    /// The whole point of the mechanism, exercised end to end: the declaration is made in configuration, no
    /// replacement is registered, and the process refuses to come up rather than quietly running the store it
    /// was told it was not running.
    /// </remarks>
    [Fact]
    public void DeclaringAnExternalStoreWithoutRegisteringOneStopsTheHost()
    {
        using (ApiTestFixture.OverrideEnvironment(
            Overrides((ProviderVariable, RefreshTokenStoreOptions.ExternalProvider))))
        {
            string report = FailureOfStartingAHost();

            report.Should().Contain(
                $"{RefreshTokenStoreOptions.SectionName}:{nameof(RefreshTokenStoreOptions.Provider)}",
                "a refusal has to name the configuration path an operator must change");
            report.Should().Contain(
                "AddInfrastructure",
                "and where the missing registration belongs, not only that one is missing");
        }
    }

    /// <summary>An unrecognised provider name stops the host before it serves.</summary>
    /// <remarks>
    /// A typo is the likeliest way a deployment ends up believing it configured a shared store. The name is
    /// one this build could never resolve, so admitting it would mean silently running the process-local store
    /// under a configuration that says otherwise.
    /// </remarks>
    [Fact]
    public void AnUnrecognisedProviderNameStopsTheHost()
    {
        using (ApiTestFixture.OverrideEnvironment(Overrides((ProviderVariable, "Redis"))))
        {
            FailureOfStartingAHost().Should().Contain(
                RefreshTokenStoreOptions.InProcessProvider,
                "the refusal has to name a value that would work, not merely reject the one supplied");
        }
    }

    /// <summary>
    /// A capacity below the deployment floor stops the host, rather than being discovered by a signed-out user.
    /// </summary>
    /// <remarks>
    /// The floor is host policy: below roughly a thousand tracked generations a single active caller can evict
    /// its own refresh family, so a small number configured with good intentions would produce apparently
    /// random sign-outs. It is refused while the host starts, where an operator is looking.
    /// </remarks>
    [Fact]
    public void ACapacityBelowTheDeploymentFloorStopsTheHost()
    {
        using (ApiTestFixture.OverrideEnvironment(Overrides((CapacityVariable, "10"))))
        {
            FailureOfStartingAHost().Should().Contain(
                nameof(RefreshTokenStoreOptions.MaximumTrackedTokens),
                "the refusal has to name the setting rather than the type that read it");
        }
    }

    /// <summary>
    /// Builds a host inside the current environment scope and returns the whole failure it produced.
    /// </summary>
    /// <returns>The exception chain rendered as one string.</returns>
    /// <remarks>
    /// <para>
    /// The chain is flattened rather than one exception type being asserted, deliberately. A refusal raised
    /// while the test host's entry point is running reaches this caller through the hosting infrastructure,
    /// which is free to wrap it, and the property under test is that the deployment does not start and is told
    /// why - not which wrapper carried the message. Asserting the wrapper would make these facts fragile
    /// against a runtime patch while proving nothing extra.
    /// </para>
    /// <para>
    /// The client is requested rather than only the factory being constructed, because a
    /// <c>WebApplicationFactory</c> composes nothing until its server is needed.
    /// </para>
    /// </remarks>
    private static string FailureOfStartingAHost()
    {
        Exception? failure = Record.Exception(() =>
        {
            using var host = new TopologyHost();
            using HttpClient client = host.CreateClient();
        });

        failure.Should().NotBeNull(
            "a deployment whose declared store is not the store it runs must not reach the point of serving");

        List<string> messages = [];
        for (Exception? current = failure; current is not null; current = current.InnerException)
        {
            messages.Add(current.Message);

            if (current is AggregateException aggregate)
            {
                messages.AddRange(aggregate.InnerExceptions.Select(inner => inner.Message));
            }
        }

        return string.Join(" | ", messages);
    }

    /// <summary>Merges the shared host configuration with this fact's own overrides.</summary>
    /// <param name="settings">The variables this fact sets.</param>
    /// <returns>The complete environment a host must be built under.</returns>
    /// <remarks>
    /// The shared configuration is carried wholesale because the refusal under test must be the ONLY reason the
    /// host declines to start: without the run's connection string and signing secret, every fact here would
    /// pass on a different failure entirely.
    /// </remarks>
    private IReadOnlyDictionary<string, string?> Overrides(
        params (string Key, string Value)[] settings)
    {
        Dictionary<string, string?> overrides =
            new(_fixture.HostConfiguration(), StringComparer.Ordinal);

        foreach ((string key, string value) in settings)
        {
            overrides[key] = value;
        }

        return overrides;
    }

    /// <summary>
    /// A host of this suite's own, so the shared fixture's host is never rebuilt under a broken configuration.
    /// </summary>
    /// <remarks>
    /// It carries no customisation beyond the environment name: the point of these facts is that the
    /// PRODUCTION composition root refuses, so anything this host changed about that composition would weaken
    /// the evidence.
    /// </remarks>
    private sealed class TopologyHost : WebApplicationFactory<Program>
    {
        /// <inheritdoc />
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            ArgumentNullException.ThrowIfNull(builder);

            builder.UseEnvironment("Testing");
        }
    }
}
