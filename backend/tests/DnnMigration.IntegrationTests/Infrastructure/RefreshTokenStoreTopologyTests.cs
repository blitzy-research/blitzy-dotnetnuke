using DnnMigration.Application.Options;
using DnnMigration.Application.Services;
using DnnMigration.Domain.Abstractions.Services;
using DnnMigration.Domain.Enums;
using DnnMigration.Infrastructure;
using DnnMigration.Infrastructure.Security;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
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

    /// <summary>
    /// A configuration still setting the removed runtime-table-creation switch stops the host, naming the
    /// deployment script that replaced it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// MIGRATION: SEC-05. <c>RefreshTokenStore:CreateTableIfMissing</c> used to let the durable store issue
    /// <c>CREATE TABLE</c> and three <c>CREATE INDEX</c> statements on first use, under whatever identity the
    /// API runs as. The table is now provisioned by a deployment step and the store only probes for it.
    /// </para>
    /// <para>
    /// THE REFUSAL IS THE POINT, because the alternative is silence: options binding says nothing about keys a
    /// class does not carry, so a deployment that had set this would have had it disregarded without a word and
    /// would have discovered the change when its first sign-in failed against an unprovisioned catalogue. The
    /// message has to name the script, or the operator is left to work out what replaced a setting that has
    /// simply stopped existing.
    /// </para>
    /// <para>
    /// Asserted against the raw CONFIGURATION rather than against the options object, which is the only place
    /// the question can be asked at all - the bound object has no such property any more, which its own unit
    /// test pins.
    /// </para>
    /// </remarks>
    [Fact]
    public void AConfigurationStillAuthorisingRuntimeTableCreationStopsTheHost()
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["ConnectionStrings:Default"] = ConnectionString,
                [FormattableString.Invariant(
                    $"{RefreshTokenStoreOptions.SectionName}:{nameof(RefreshTokenStoreOptions.Provider)}")]
                    = RefreshTokenStoreOptions.InProcessProvider,
                [FormattableString.Invariant(
                    $"{RefreshTokenStoreOptions.SectionName}:{RefreshTokenStoreOptions.RemovedCreateTableSetting}")]
                    = "true",
            })
            .Build();

        ServiceCollection services = new();

        Action compose = () => services.AddInfrastructure(configuration);

        compose.Should().Throw<OptionsValidationException>()
            .Which.Failures.Should().ContainMatch(
                "*docker/sql/refresh-token-store.sql*",
                "a setting that no longer exists must be refused with the deployment step that replaced it, "
                + "not ignored");
    }

    /// <summary>
    /// A production deployment on the replica-local store that has not acknowledged running one instance is
    /// refused, and the refusal names both remedies.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠ THE FAILURE THE THREE CHECKS ABOVE CANNOT SEE. MIGRATION: SEC-06. Each of those catches a deployment
    /// whose declaration and whose container disagree. This one is the case where they agree perfectly and the
    /// answer is still wrong: production, the shipped default, and more than one replica. That needed no code
    /// change, no configuration change and produced no warning - a sign-out on replica A left the family
    /// exchangeable on replica B, a restart forgot every family issued, and both present as intermittent session
    /// behaviour no log explains.
    /// </para>
    /// <para>
    /// The refusal must name BOTH remedies, because a deployment that reaches it has a genuine choice to make -
    /// become durable, or state the constraint - and being told only that it is wrong leaves it guessing.
    /// </para>
    /// </remarks>
    [Fact]
    public void AProductionDeploymentOnAReplicaLocalStoreWithoutTheAcknowledgementIsRefused()
    {
        using ServiceProvider provider = Compose(
            environment: Environments.Production);

        Action validate = () => DependencyInjection.ValidateRefreshTokenStoreTopology(provider);

        InvalidOperationException refusal = validate.Should().Throw<InvalidOperationException>().Which;

        refusal.Message.Should().Contain(
            nameof(RefreshTokenStoreOptions.AcknowledgeSingleInstance),
            "one remedy is to state that a single instance is running");
        refusal.Message.Should().Contain(
            RefreshTokenStoreOptions.SqlServerProvider,
            "and the other is to become durable and shared, which is the better answer for any deployment "
            + "that actually has replicas");
        refusal.Message.Should().Contain(
            "docker/sql/refresh-token-store.sql",
            "the durable remedy needs a table, and the operator must be told what provisions it");
    }

    /// <summary>The acknowledgement lets a production host start on the shipped process-local store.</summary>
    /// <remarks>
    /// The first of the two satisfying answers, and the one the shipped container topology uses: a single
    /// instance is genuinely what <c>docker/docker-compose.yml</c> starts, so the claim is true of it and the
    /// file makes it on the record.
    /// </remarks>
    [Fact]
    public void AProductionDeploymentThatAcknowledgesASingleInstanceStarts()
    {
        using ServiceProvider provider = Compose(
            configureSettings: settings => settings.AcknowledgeSingleInstance = true,
            environment: Environments.Production);

        Action validate = () => DependencyInjection.ValidateRefreshTokenStoreTopology(provider);

        validate.Should().NotThrow();
    }

    /// <summary>
    /// A store that reports itself authoritative across replicas satisfies the invariant on its own merits,
    /// with no acknowledgement.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The second satisfying answer, and the reason the check keys on
    /// <see cref="IRefreshTokenStore.IsAuthoritativeAcrossReplicas"/> rather than on a type test. A type test
    /// would have exempted every deployment-supplied store without asking it anything - including the
    /// replica-local ones this check exists to catch - while holding a genuinely shared store to a ceremony it
    /// does not need. Asking the contract lets each store answer for itself.
    /// </para>
    /// <para>
    /// The substitute here is the same stand-in the substitution facts above use, and it reports
    /// <see langword="true"/> for exactly this reason. Declaring <c>External</c> alongside it is required by the
    /// three checks above, so this fact composes a topology that is coherent in every other respect - which is
    /// what makes it evidence about the invariant rather than about a mismatch.
    /// </para>
    /// </remarks>
    [Fact]
    public void AProductionDeploymentOnAReplicaSafeStoreStartsWithoutTheAcknowledgement()
    {
        using ServiceProvider provider = Compose(
            provider: RefreshTokenStoreOptions.ExternalProvider,
            registerAfter: services => services
                .AddSingleton<IRefreshTokenStore, SubstituteRefreshTokenStore>(),
            environment: Environments.Production);

        Action validate = () => DependencyInjection.ValidateRefreshTokenStoreTopology(provider);

        validate.Should().NotThrow(
            "a store that is shared between replicas and survives a restart has nothing to acknowledge");
    }

    /// <summary>Non-production environments do not require the acknowledgement.</summary>
    /// <param name="environmentName">The environment to compose under.</param>
    /// <remarks>
    /// Deliberate, and the reason is about what an acknowledgement MEANS rather than about convenience. The
    /// failure it guards is a production scale-out; demanding the same ceremony on a developer machine and in
    /// every test run would make it a value that is always set, and an acknowledgement that is always set
    /// records nothing. Pinning the exemption also keeps it from being widened by accident into production.
    /// </remarks>
    [Theory]
    [InlineData("Development")]
    [InlineData("Staging")]
    public void ANonProductionDeploymentDoesNotRequireTheAcknowledgement(string environmentName)
    {
        using ServiceProvider provider = Compose(environment: environmentName);

        Action validate = () => DependencyInjection.ValidateRefreshTokenStoreTopology(provider);

        validate.Should().NotThrow();
    }

    /// <summary>Composes the infrastructure graph with usable token settings.</summary>
    /// <param name="provider">The refresh-store provider to declare.</param>
    /// <param name="registerAfter">
    /// Registrations applied AFTER <c>AddInfrastructure</c>, which is where a deployment substituting a store
    /// puts its own registration.
    /// </param>
    /// <param name="configureSettings">Mutates the store settings before they are registered.</param>
    /// <param name="environment">
    /// The hosting environment to compose under, or <see langword="null"/> to register none. Null is the
    /// default and is what every pre-existing fact here uses: the graph is a bare service collection rather
    /// than a host, so nothing registers an environment unless a fact is ABOUT the environment.
    /// </param>
    /// <returns>The built provider; the caller disposes it.</returns>
    /// <remarks>
    /// The JWT settings are supplied as an already-created options object because the Api layer owns the
    /// binding of that section and is not part of this graph; without them the store would refuse to be
    /// constructed on an empty signing secret, which is a different fact asserted in its own suite.
    /// </remarks>
    private static ServiceProvider Compose(
        string provider = RefreshTokenStoreOptions.InProcessProvider,
        Action<IServiceCollection>? registerAfter = null,
        Action<RefreshTokenStoreOptions>? configureSettings = null,
        string? environment = null)
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

        if (environment is not null)
        {
            services.AddSingleton<IHostEnvironment>(new StubHostEnvironment(environment));
        }

        registerAfter?.Invoke(services);

        return services.BuildServiceProvider();
    }

    /// <summary>A hosting environment carrying only the name the topology check reads.</summary>
    /// <remarks>
    /// The three path members answer with values that address nothing on disk, because nothing in this graph
    /// reads a file: the only member under test is the NAME, and a stub offering plausible paths would invite a
    /// later fact to depend on one.
    /// </remarks>
    private sealed class StubHostEnvironment : IHostEnvironment
    {
        /// <summary>Initialises a new instance of the <see cref="StubHostEnvironment"/> class.</summary>
        /// <param name="environmentName">The environment name to report.</param>
        public StubHostEnvironment(string environmentName) => EnvironmentName = environmentName;

        /// <inheritdoc />
        public string EnvironmentName { get; set; }

        /// <inheritdoc />
        public string ApplicationName { get; set; } = "DnnMigration.Tests";

        /// <inheritdoc />
        public string ContentRootPath { get; set; } = "/nonexistent";

        /// <inheritdoc />
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
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

        /// <inheritdoc />
        public Task<RefreshTokenPurgeResult> PurgeSubjectAsync(
            RefreshTokenPurgeScope scope,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("The substitute store is registered, not exercised.");

        /// <inheritdoc />
        /// <remarks>
        /// PRIV-02. Answers with an empty reclamation rather than refusing, and it is the ONE member that does.
        /// The reclamation sweep is a hosted service that runs on a schedule in every host these facts build,
        /// so a substitute that threw here would raise out of a background timer during an unrelated
        /// assertion - a failure attributed to whichever fact happened to be running. Reporting "nothing to
        /// reclaim" is also true of a store that holds nothing.
        /// </remarks>
        public Task<RefreshTokenPurgeResult> PurgeRetiredAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(RefreshTokenPurgeResult.NothingHeld());
    }
}
