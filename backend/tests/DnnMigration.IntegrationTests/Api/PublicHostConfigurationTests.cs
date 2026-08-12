using DnnMigration.Api.Extensions;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DnnMigration.IntegrationTests.Api;

/// <summary>
/// Verifies that a deployment configured with a documentation host name is refused at start-up, and that the
/// names a real deployment legitimately uses are not.
/// </summary>
/// <remarks>
/// <para>
/// MIGRATION: THE PUBLIC IDENTITY OF A DEPLOYMENT WAS AN EXAMPLE WRITTEN INTO TRACKED FILES.
/// <c>docker/docker-compose.tls.yml</c> set <c>AllowedHosts</c> to <c>dnn.example.com;localhost;127.0.0.1</c>
/// and the mounted proxy block hard-coded the same name in both of its <c>server_name</c> directives, while
/// only the cross-origin origin read a variable. A deployment that set that variable - the documented
/// workflow - therefore ran with a redirect scoped to a name its browsers never send, a certificate matching
/// no server name, and a host filter that answered <b>400</b> to all of its own traffic. Every one of those
/// required editing a tracked file to avoid.
/// </para>
/// <para>
/// The overlay now derives all four facts from one required <c>DNN_PUBLIC_HOST</c> value, compose refuses to
/// create a container when it is unset or blank, and these facts pin the remaining half: that the value most
/// likely to be present - the template's own illustration - is refused before the process serves anything.
/// </para>
/// <para>
/// The registration surface is exercised rather than a copy of the rules, so a future edit that stops
/// validating the host filter, or that widens the rejection into names a private deployment uses, fails here.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
public sealed class PublicHostConfigurationTests
{
    /// <summary>A documentation host name in the host filter stops the host from starting.</summary>
    /// <param name="reservedHost">A name reserved for documentation, in the shapes a template ships.</param>
    /// <remarks>
    /// The loopback entries are included in every case, because the container probe depends on them and a
    /// deployment that made this mistake would still have had them: the defect is one entry among several,
    /// not a wholly empty setting.
    /// </remarks>
    [Theory]
    [InlineData("dnn.example.com")]
    [InlineData("example.com")]
    [InlineData("admin.example.net")]
    [InlineData("portal.example.org")]
    [InlineData("admin.acme.example")]
    [InlineData("*.example.com")]
    [InlineData("dnn.example.com:8443")]
    [InlineData("changeme")]
    [InlineData("your-domain.com")]
    public void AReservedHostInTheHostFilterStopsTheHost(string reservedHost)
    {
        IConfiguration configuration = Configuration(
            (ServiceCollectionExtensions.PermittedHostsSectionName,
                $"{reservedHost};localhost;127.0.0.1"));

        Register(configuration).Should().Throw<InvalidOperationException>()
            .WithMessage($"*{ServiceCollectionExtensions.PermittedHostsSectionName}*")
            .And.Message.Should().Contain(
                "DNN_PUBLIC_HOST",
                "the message has to name the setting an operator must change, not merely the key it landed in");
    }

    /// <summary>The names a real deployment uses are accepted, including the reserved test domains.</summary>
    /// <param name="permittedHosts">A host-filter value a deployment legitimately ships.</param>
    /// <remarks>
    /// This is the more important half. RFC 2606 reserves <c>.test</c>, <c>.invalid</c> and <c>.localhost</c>
    /// alongside the documentation domains, and a validator that refused them would refuse a private
    /// deployment on an internal certificate authority, the loopback entries the container health probe
    /// depends on, and the wildcard that <c>appsettings.json</c> ships as its default - converting a
    /// hardening measure into an outage.
    /// </remarks>
    [Theory]
    [InlineData("*")]
    [InlineData("localhost;127.0.0.1")]
    [InlineData("admin.acme.test;localhost;127.0.0.1")]
    [InlineData("dnn.internal.acme.invalid;localhost")]
    [InlineData("admin.acme.com;localhost;127.0.0.1")]
    [InlineData("example-hosting.com;localhost")]
    [InlineData("changemakers.org;localhost")]
    [InlineData("[::1];localhost")]
    public void ADeployableHostFilterDoesNotStopTheHost(string permittedHosts)
    {
        IConfiguration configuration = Configuration(
            (ServiceCollectionExtensions.PermittedHostsSectionName, permittedHosts));

        Register(configuration).Should().NotThrow(
            "a name a deployment can actually serve must never be refused by a guard aimed at illustrations");
    }

    /// <summary>An absent or blank host filter is left to the framework's own default.</summary>
    /// <param name="value">The absent-or-blank forms a template and an environment can deliver.</param>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AnAbsentHostFilterDoesNotStopTheHost(string? value)
    {
        IConfiguration configuration = value is null
            ? Configuration()
            : Configuration((ServiceCollectionExtensions.PermittedHostsSectionName, value));

        Register(configuration).Should().NotThrow();
    }

    /// <summary>A documentation host in a permitted browser origin stops the host from starting.</summary>
    /// <param name="reservedOrigin">An origin naming a documentation host.</param>
    /// <remarks>
    /// The cross-origin allow-list is derived from the same deployment value as the host filter, so the same
    /// illustration reaches both. It is refused in both places for one reason: a browser can never send it,
    /// so the policy it configures would admit nothing while appearing configured.
    /// </remarks>
    [Theory]
    [InlineData("https://dnn.example.com")]
    [InlineData("https://admin.acme.example")]
    [InlineData("http://changeme")]
    public void AReservedOriginStopsTheHost(string reservedOrigin)
    {
        IConfiguration configuration = Configuration(("Cors:AllowedOrigins:0", reservedOrigin));

        RegisterCors(configuration).Should().Throw<InvalidOperationException>()
            .WithMessage("*Cors:AllowedOrigins*");
    }

    /// <summary>A deployable origin is accepted, including one on a reserved TEST domain.</summary>
    /// <param name="origin">An origin a deployment legitimately ships.</param>
    [Theory]
    [InlineData("https://admin.acme.test")]
    [InlineData("http://localhost:4200")]
    [InlineData("https://localhost:4443")]
    [InlineData("https://admin.acme.com")]
    public void ADeployableOriginDoesNotStopTheHost(string origin)
    {
        IConfiguration configuration = Configuration(("Cors:AllowedOrigins:0", origin));

        RegisterCors(configuration).Should().NotThrow();
    }

    private static IConfiguration Configuration(params (string Key, string? Value)[] entries) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(entries.ToDictionary(
                entry => entry.Key,
                entry => entry.Value,
                StringComparer.Ordinal))
            .Build();

    /// <summary>Runs the registration that validates the host filter.</summary>
    /// <param name="configuration">The configuration under test.</param>
    /// <returns>The action to assert on.</returns>
    /// <remarks>
    /// The host filter is validated eagerly during registration rather than inside a deferred options
    /// callback, so no resolution step is needed: a deployment must learn about this before the host starts
    /// serving, not on the first request that happens to build the options.
    /// </remarks>
    private static Action Register(IConfiguration configuration) => () =>
    {
        ServiceCollection services = new();
        services.AddApiServices(configuration);
    };

    /// <summary>Runs the cross-origin registration, which reads and validates the origin list.</summary>
    /// <param name="configuration">The configuration under test.</param>
    /// <returns>The action to assert on.</returns>
    private static Action RegisterCors(IConfiguration configuration) => () =>
    {
        ServiceCollection services = new();
        services.AddSpaCors(configuration);
    };
}
