using DnnMigration.Application.Options;
using DnnMigration.Application.Services;
using DnnMigration.Domain.Abstractions.Services;
using DnnMigration.Domain.Enums;
using DnnMigration.Infrastructure;
using DnnMigration.Infrastructure.Security;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace DnnMigration.IntegrationTests.Infrastructure;

/// <summary>
/// Verifies that a deployment can replace the refresh-token store without editing the Infrastructure layer,
/// and that the host refuses to start whenever the store a deployment DECLARES is not the store the container
/// resolves.
/// </summary>
/// <remarks>
/// <para>
/// WHY THIS SUITE EXISTS. The store this solution ships is process-local: refresh state is neither shared
/// between replicas nor carried across a restart, because AAP rule T4 forbids adding a table to the existing
/// DotNetNuke schema, AAP 0.6 freezes a dependency inventory with no distributed-cache client, and AAP 0.9.3
/// reproduces a two-service container topology verbatim. Two claims are made about that arrangement -
/// <c>README.md</c> and <c>MIGRATION_NOTES.md</c> both say a deployment needing cross-process continuity may
/// supply its own store behind <c>IRefreshTokenStore</c>, and the code says a mismatch between the declared
/// and the registered store is a start-up failure - and a claim about substitutability that no test exercises
/// is an assurance rather than a property. Every fact below is one of those two claims.
/// </para>
/// <para>
/// The container is composed here rather than through a host, because the question is about REGISTRATION
/// ORDER and nothing else: no database is reached, no request is served, and the whole graph is built and
/// interrogated in memory.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
public class RefreshTokenStoreTopologyTests
{
    /// <summary>A structurally complete connection string; nothing below opens a connection with it.</summary>
    private const string ConnectionString =
        "Server=db.example.invalid,1433;Database=DotNetNuke;User Id=dnn_app;Password=Sfx7!qLp2vRz;Encrypt=True";

    /// <summary>
    /// The store <c>AddInfrastructure</c> registers is this solution's, and both service types share the one
    /// instance.
    /// </summary>
    /// <remarks>
    /// The positive control, and the second half is not decoration: with a process-local store the state IS
    /// the instance, so a second instance behind the contract would make a token issued through one unknown
    /// to the other.
    /// </remarks>
    [Fact]
    public void TheDefaultRegistrationIsThisSolutionsStoreAndIsShared()
    {
        using ServiceProvider provider = Compose();

        IRefreshTokenStore resolved = provider.GetRequiredService<IRefreshTokenStore>();

        resolved.Should().BeOfType<RefreshTokenStore>();
        resolved.Should().BeSameAs(
            provider.GetRequiredService<RefreshTokenStore>(),
            "the contract is registered through a factory over the concrete singleton, so there is one store");
        resolved.Should().BeSameAs(
            provider.GetRequiredService<IRefreshTokenStore>(),
            "a second resolution must not produce a second, empty store");
    }

    /// <summary>The default topology starts: nothing has to be configured for the shipped store.</summary>
    [Fact]
    public void TheDefaultTopologyIsAccepted()
    {
        using ServiceProvider provider = Compose();

        Action validate = () => DependencyInjection.ValidateRefreshTokenStoreTopology(provider);

        validate.Should().NotThrow();
    }

    /// <summary>
    /// A deployment registering its own store after <c>AddInfrastructure</c> wins, with no change to this
    /// repository's Infrastructure layer.
    /// </summary>
    /// <remarks>
    /// This is the documented escape from the process-local limitation, stated as an executable fact. It works
    /// because the container resolves the LAST registration of a service and because both consumers depend on
    /// the contract - the two properties the next fact pins down.
    /// </remarks>
    [Fact]
    public void AStoreRegisteredAfterAddInfrastructureReplacesTheShippedOne()
    {
        using ServiceProvider provider = Compose(
            RefreshTokenStoreOptions.ExternalProvider,
            services => services.AddSingleton<IRefreshTokenStore, SubstituteRefreshTokenStore>());

        provider.GetRequiredService<IRefreshTokenStore>()
            .Should().BeOfType<SubstituteRefreshTokenStore>(
                "the last registration of a service is the one the container hands out");

        Action validate = () => DependencyInjection.ValidateRefreshTokenStoreTopology(provider);

        validate.Should().NotThrow("a declared external store that is actually registered is a valid topology");
    }

    /// <summary>
    /// Both consumers of the store depend on the CONTRACT, which is what makes the substitution reach them.
    /// </summary>
    /// <remarks>
    /// Asserted structurally rather than behaviourally, because that is where the property lives: a consumer
    /// that took the concrete <c>RefreshTokenStore</c> would keep using the shipped store however the
    /// container was reconfigured, and no runtime assertion about the substituted store would reveal it.
    /// </remarks>
    [Fact]
    public void NeitherConsumerBindsToTheConcreteStore()
    {
        foreach (Type consumer in new[] { typeof(JwtTokenService), typeof(AuthService) })
        {
            IEnumerable<Type> parameters = consumer
                .GetConstructors()
                .SelectMany(constructor => constructor.GetParameters())
                .Select(parameter => parameter.ParameterType);

            parameters.Should().NotContain(
                typeof(RefreshTokenStore),
                $"{consumer.Name} must reach the store through IRefreshTokenStore for substitution to work");
            parameters.Should().Contain(
                typeof(IRefreshTokenStore),
                $"{consumer.Name} is a consumer of the store and must name the contract");
        }
    }

    /// <summary>
    /// Declaring an external store while registering none is refused, naming the key and both remedies.
    /// </summary>
    /// <remarks>
    /// The failure this whole mechanism exists for: a deployment that believes it has a shared store, scales
    /// out behind a load balancer, and finds refresh succeeding or failing according to which replica answers.
    /// </remarks>
    [Fact]
    public void DeclaringAnExternalStoreWithoutRegisteringOneIsRefused()
    {
        using ServiceProvider provider = Compose(RefreshTokenStoreOptions.ExternalProvider);

        Action validate = () => DependencyInjection.ValidateRefreshTokenStoreTopology(provider);

        string message = validate.Should().Throw<InvalidOperationException>().Which.Message;

        message.Should().Contain(
            $"{RefreshTokenStoreOptions.SectionName}:{nameof(RefreshTokenStoreOptions.Provider)}",
            "a refusal has to name the configuration path an operator must change");
        message.Should().Contain("AddInfrastructure", "and the corrective action, not only the problem");
        message.Should().Contain(RefreshTokenStoreOptions.InProcessProvider);
    }

    /// <summary>
    /// Overriding the store while still declaring the in-process one is refused in the other direction.
    /// </summary>
    /// <remarks>
    /// The mirror-image failure, and the reason the check is symmetric: a deployment whose configuration, whose
    /// health report and whose operators all describe a store the process is not running has no way to notice
    /// until something depends on the difference.
    /// </remarks>
    [Fact]
    public void OverridingTheStoreWhileDeclaringTheInProcessOneIsRefused()
    {
        using ServiceProvider provider = Compose(
            RefreshTokenStoreOptions.InProcessProvider,
            services => services.AddSingleton<IRefreshTokenStore, SubstituteRefreshTokenStore>());

        Action validate = () => DependencyInjection.ValidateRefreshTokenStoreTopology(provider);

        validate.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain(
                RefreshTokenStoreOptions.ExternalProvider,
                "the remedy is to declare the store that is actually registered");
    }

    /// <summary>An unrecognised provider name is refused before the container is even consulted.</summary>
    [Fact]
    public void AnUnrecognisedProviderNameIsRefused()
    {
        using ServiceProvider provider = Compose("Redis");

        Action validate = () => DependencyInjection.ValidateRefreshTokenStoreTopology(provider);

        validate.Should().Throw<OptionsValidationException>()
            .Which.Failures.Should().ContainMatch("*does not recognise*");
    }

    /// <summary>
    /// The topology check also forces the store to be constructed, so unusable store settings abort start-up.
    /// </summary>
    /// <remarks>
    /// A deliberate secondary effect of resolving the contract here: without it, a capacity of zero would be
    /// discovered at a caller's first sign-in rather than while the host was starting.
    /// </remarks>
    [Fact]
    public void UnusableStoreSettingsAbortStartUpRatherThanTheFirstSignIn()
    {
        using ServiceProvider provider = Compose(
            RefreshTokenStoreOptions.InProcessProvider,
            configureSettings: settings => settings.MaximumTrackedTokens = 0);

        Action validate = () => DependencyInjection.ValidateRefreshTokenStoreTopology(provider);

        validate.Should().Throw<OptionsValidationException>()
            .Which.Failures.Should().ContainMatch(
                $"*{nameof(RefreshTokenStoreOptions.MaximumTrackedTokens)}*");
    }

    /// <summary>Composes the infrastructure graph with usable token settings.</summary>
    /// <param name="provider">The refresh-store provider to declare.</param>
    /// <param name="registerAfter">
    /// Registrations applied AFTER <c>AddInfrastructure</c>, which is where a deployment substituting a store
    /// puts its own registration.
    /// </param>
    /// <param name="configureSettings">Mutates the store settings before they are registered.</param>
    /// <returns>The built provider; the caller disposes it.</returns>
    /// <remarks>
    /// The JWT settings are supplied as an already-created options object because the Api layer owns the
    /// binding of that section and is not part of this graph; without them the store would refuse to be
    /// constructed on an empty signing secret, which is a different fact asserted in its own suite.
    /// </remarks>
    private static ServiceProvider Compose(
        string provider = RefreshTokenStoreOptions.InProcessProvider,
        Action<IServiceCollection>? registerAfter = null,
        Action<RefreshTokenStoreOptions>? configureSettings = null)
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["ConnectionStrings:Default"] = ConnectionString,
            })
            .Build();

        ServiceCollection services = new();
        services.AddInfrastructure(configuration);

        services.AddSingleton<IOptions<JwtOptions>>(Options.Create(new JwtOptions
        {
            Secret = "unit-test-signing-secret-with-enough-entropy-0123456789",
            Issuer = "DnnMigration",
            Audience = "DnnMigration",
            ExpirationMinutes = 30,
            RefreshTokenExpirationDays = 7,
            RefreshTokenAbsoluteExpirationDays = 30,
        }));

        RefreshTokenStoreOptions settings = new() { Provider = provider };
        configureSettings?.Invoke(settings);
        services.AddSingleton<IOptions<RefreshTokenStoreOptions>>(Options.Create(settings));

        registerAfter?.Invoke(services);

        return services.BuildServiceProvider();
    }

    /// <summary>
    /// A stand-in for a deployment-supplied store, registered but never exercised by this suite.
    /// </summary>
    /// <remarks>
    /// It answers every member with a refusal rather than with plausible behaviour, deliberately: these facts
    /// are about which implementation the container hands out, and a substitute that appeared to work could let
    /// a fact pass while resolving the wrong instance.
    /// </remarks>
    private sealed class SubstituteRefreshTokenStore : IRefreshTokenStore
    {
        /// <inheritdoc />
        /// <remarks>
        /// <see langword="true"/>, because that is the only honest answer for a stand-in whose whole purpose
        /// is to represent a deployment-supplied store: the contract admits <see langword="true"/> only for
        /// state every replica shares and a restart survives, and a substitute claiming otherwise would be
        /// indistinguishable from this solution's own process-local store in exactly the assertion these
        /// facts make.
        /// </remarks>
        public bool IsAuthoritativeAcrossReplicas => true;

        /// <inheritdoc />
        public Task<RefreshTokenIssueResult> IssueAsync(
            RefreshTokenSubject subject,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("The substitute store is registered, not exercised.");

        /// <inheritdoc />
        public Task<RefreshTokenInspection> InspectAsync(
            string refreshToken,
            string clientBinding,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("The substitute store is registered, not exercised.");

        /// <inheritdoc />
        public Task<RefreshTokenRotationResult> RotateAsync(
            string refreshToken,
            string clientBinding,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("The substitute store is registered, not exercised.");

        /// <inheritdoc />
        public Task<RefreshTokenOutcome> RevokeAsync(
            string refreshToken,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("The substitute store is registered, not exercised.");

        /// <inheritdoc />
        public Task<RefreshTokenOutcome> RevokeAllForUserAsync(
            int userId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("The substitute store is registered, not exercised.");
    }
}
