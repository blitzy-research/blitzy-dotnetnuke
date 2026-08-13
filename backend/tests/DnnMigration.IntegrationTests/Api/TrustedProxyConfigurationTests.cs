using DnnMigration.Api.Extensions;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DnnMigration.IntegrationTests.Api;

/// <summary>
/// Verifies that one configured trusted-hop value means one thing: the predicate that decides whether the
/// forwarded-header stage is registered and the registration that parses the entries read the same
/// normalised list.
/// </summary>
[Trait("Category", "Integration")]
public sealed class TrustedProxyConfigurationTests
{
    /// <summary>A section whose entries are all blank declares no trust and does not stop the host.</summary>
    /// <param name="sectionName">The trusted-hop section under test.</param>
    /// <param name="blankEntry">The blank form a deployment template can ship.</param>
    [Theory]
    [InlineData("Proxy:KnownProxies", "")]
    [InlineData("Proxy:KnownProxies", "   ")]
    [InlineData("Proxy:KnownNetworks", "")]
    [InlineData("Proxy:KnownNetworks", "   ")]
    public void ABlankEntryDeclaresNoTrustAndDoesNotStopTheHost(string sectionName, string blankEntry)
    {
        IConfiguration configuration = Configuration((sectionName + ":0", blankEntry));

        ServiceCollectionExtensions.HasTrustedProxies(configuration).Should().BeFalse(
            "a template entry left empty has not named a hop, so nothing may be trusted");

        Register(configuration).Should().NotThrow(
            "the same blank entry that reads as no trust must not also refuse to start the host");
    }

    /// <summary>Surrounding whitespace does not change what a declared hop means.</summary>
    /// <param name="sectionName">The trusted-hop section under test.</param>
    /// <param name="paddedEntry">A usable value carrying whitespace, as a compose file can deliver it.</param>
    [Theory]
    [InlineData("Proxy:KnownProxies", " 172.28.0.10 ")]
    [InlineData("Proxy:KnownNetworks", " 172.16.0.0/12 ")]
    public void APaddedEntryIsTrustedAndParsed(string sectionName, string paddedEntry)
    {
        IConfiguration configuration = Configuration((sectionName + ":0", paddedEntry));

        ServiceCollectionExtensions.HasTrustedProxies(configuration).Should().BeTrue();
        Register(configuration).Should().NotThrow();
    }

    /// <summary>
    /// A blank entry alongside a usable one is dropped rather than refused, and the usable one still
    /// counts.
    /// </summary>
    /// <remarks>
    /// The mixed list is the shape a half-edited template leaves: one hop named, a second slot left empty.
    /// Read without the blank-entry filter, this configuration counts as trusted and then stops the host
    /// on the empty slot.
    /// </remarks>
    [Fact]
    public void AMixedListKeepsTheUsableEntryAndDropsTheBlankOne()
    {
        IConfiguration configuration = Configuration(
            ("Proxy:KnownProxies:0", "172.28.0.10"),
            ("Proxy:KnownProxies:1", "   "));

        ServiceCollectionExtensions.HasTrustedProxies(configuration).Should().BeTrue();
        Register(configuration).Should().NotThrow();
    }

    /// <summary>An entry that is neither blank nor an address still stops the host.</summary>
    /// <param name="sectionName">The trusted-hop section under test.</param>
    /// <param name="malformedEntry">A value an operator meant and got wrong.</param>
    /// <remarks>
    /// The distinction the normalisation must preserve: a blank entry is a hop nobody declared, and a
    /// malformed one is a hop somebody declared incorrectly. Silently dropping the second would produce a
    /// deployment that starts, appears configured, and partitions its credential rate limiter on the
    /// proxy's own address - the exact failure the setting exists to prevent, with nothing in the log.
    /// </remarks>
    [Theory]
    [InlineData("Proxy:KnownProxies", "not-an-address")]
    [InlineData("Proxy:KnownNetworks", "172.16.0.0")]
    [InlineData("Proxy:KnownNetworks", "172.16.0.0/99")]
    public void AMalformedEntryStillStopsTheHost(string sectionName, string malformedEntry)
    {
        IConfiguration configuration = Configuration((sectionName + ":0", malformedEntry));

        ServiceCollectionExtensions.HasTrustedProxies(configuration).Should().BeTrue(
            "a non-blank entry is a declaration, however wrong");

        Register(configuration).Should().Throw<InvalidOperationException>()
            .WithMessage("*" + sectionName + "*");
    }

    /// <summary>An absent section declares no trust.</summary>
    [Fact]
    public void AnAbsentSectionDeclaresNoTrust()
    {
        IConfiguration configuration = Configuration();

        ServiceCollectionExtensions.HasTrustedProxies(configuration).Should().BeFalse();
        Register(configuration).Should().NotThrow();
    }

    private static IConfiguration Configuration(params (string Key, string? Value)[] entries) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(entries.ToDictionary(
                entry => entry.Key,
                entry => entry.Value,
                StringComparer.Ordinal))
            .Build();

    /// <summary>
    /// Runs the forwarded-header registration the pipeline depends on, so a parse refusal surfaces here.
    /// </summary>
    /// <param name="configuration">The configuration under test.</param>
    /// <returns>The action to assert on.</returns>
    /// <remarks>
    /// The options are RESOLVED rather than merely registered, because <c>Configure</c> defers the
    /// callback: a parse failure inside it is raised when the options instance is first built, not when it
    /// is registered, so an unresolved registration would report success for a value that stops the host.
    /// </remarks>
    private static Action Register(IConfiguration configuration) => () =>
    {
        ServiceCollection services = new();
        services.AddApiServices(configuration);

        using ServiceProvider provider = services.BuildServiceProvider();
        _ = provider
            .GetRequiredService<Microsoft.Extensions.Options.IOptions<
                Microsoft.AspNetCore.Builder.ForwardedHeadersOptions>>()
            .Value;
    };
}
