// The 269 MustOverride members of that one abstract class - measured, in a 397-line file are DECOMPOSED BY
// AGGREGATE BOUNDARY into the nine focused repository interfaces registered below, and ONLY THE IN-SCOPE
// SUBSET of those members is realised.
using System.Globalization;
using DnnMigration.Application.Abstractions;
using DnnMigration.Application.Options;
using DnnMigration.Domain.Abstractions.Repositories;
using DnnMigration.Domain.Abstractions.Services;
using DnnMigration.Infrastructure.HealthChecks;
using DnnMigration.Infrastructure.Persistence;
using DnnMigration.Infrastructure.Repositories;
using DnnMigration.Infrastructure.Security;
using DnnMigration.Infrastructure.Services;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace DnnMigration.Infrastructure;

/// <summary>Registers this layer's persistence, security and platform services with a service collection.</summary>
public static class DependencyInjection
{
    /// <summary>The name under which the database connectivity probe is registered.</summary>
    private const string DatabaseHealthCheckName = "database";

    /// <summary>The name under which failed audit delivery is surfaced.</summary>
    private const string AuditPipelineHealthCheckName = "audit-pipeline";

    /// <summary>
    /// The name under which the refresh-token store's identity, replica safety and capacity are surfaced.
    /// </summary>
    private const string RefreshTokenStoreHealthCheckName = "refresh-token-store";

    /// <summary>Tag marking a probe as a READINESS signal rather than a liveness one.</summary>
    private const string ReadinessTag = "ready";

    /// <summary>Tag naming the dependency a probe examines.</summary>
    private const string DatabaseTag = "db";

    /// <summary>Tag marking a probe as a process-local liveness signal.</summary>
    private const string LivenessTag = "live";

    /// <summary>Tag naming the accountability trail a probe examines.</summary>
    private const string AuditTag = "audit";

    /// <summary>Tag naming the process-local session state a probe examines.</summary>
    /// <remarks>
    /// Distinct from <see cref="AuditTag"/> so that a monitor can select the state-locality report on its
    /// own - which matters because it is the probe a deployment consults before it considers running a
    /// second API instance.
    /// </remarks>
    private const string SessionStateTag = "session-state";

    /// <summary>Greatest time the dependency probe is allowed before the infrastructure abandons it.</summary>
    /// <remarks>
    /// Two seconds, so the probe can time out and the endpoint still answers inside the five seconds the
    /// container's own probe allows before it counts the attempt as a failure.
    /// </remarks>
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(2);
    /// <summary>The schema holding the migrations-history table.</summary>
    private const string MigrationsHistorySchema = "dbo";

    /// <summary>The migrations-history table name.</summary>
    private const string MigrationsHistoryTableName = "__EFMigrationsHistory";

    /// <summary>
    /// Largest number of entries the shared memory cache will hold before the runtime evicts the least
    /// recently used of them.
    /// </summary>
    /// <remarks>
    /// Fifty thousand is far above what this application caches.
    /// </remarks>
    private const long CacheEntryLimit = 50_000;

    /// <summary>Registers every service this layer implements.</summary>
    /// <param name="services">The collection to add to.</param>
    /// <param name="configuration">The host configuration, read only for the connection string.</param>
    /// <returns>The same collection, so registration can be chained.</returns>
    /// <exception cref="ArgumentNullException">Either argument is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">No connection string is configured.</exception>
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        string connectionString = ReadConnectionString(configuration);

        AddPersistence(services, connectionString);
        AddRepositories(services);
        AddTenantContext(services);
        AddSecurity(services, configuration);
        AddPlatformServices(services);

        AddHealthChecks(services);

        return services;
    }

    /// <summary>
    /// Reads the connection string, failing fast when it is absent, unusable, or still carrying a
    /// documented placeholder.
    /// </summary>
    /// <param name="configuration">The host configuration.</param>
    /// <returns>The configured connection string.</returns>
    /// <exception cref="InvalidOperationException">
    /// No connection string is configured, it is not parseable, it does not name both a server and a
    /// database, it names no way to authenticate, or any of its values is a documented placeholder.
    /// </exception>
    /// <remarks>
    /// Four rules, and each rejects a value that would otherwise fail on the first request instead. The
    /// value must PARSE - a malformed keyword list is a configuration error, not a connection error.
    /// </remarks>
    private static string ReadConnectionString(IConfiguration configuration)
    {
        const string KeyDescription =
            "Supply it as 'ConnectionStrings:Default' in configuration, or as the environment "
            + "variable 'ConnectionStrings__Default' in a container.";

        string? connectionString = configuration.GetConnectionString("Default");

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "No database connection string is configured. " + KeyDescription);
        }

        SqlConnectionStringBuilder builder;
        try
        {
            builder = new SqlConnectionStringBuilder(connectionString);
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException)
        {
            // Deliberately not chained as an inner exception: the builder's own message quotes the
            // fragment it could not parse, which for a connection string may be the credential.
            throw new InvalidOperationException(
                "The configured database connection string is not a valid SQL Server connection "
                + "string. " + KeyDescription
                + " The value is not reproduced here because it carries a credential.");
        }

        if (ContainsPlaceholder(builder))
        {
            throw new InvalidOperationException(
                "The configured database connection string still carries a placeholder value from a "
                + "deployment template, so it cannot reach a database. Replace every placeholder with "
                + "the real server, database and credential before starting the API. " + KeyDescription);
        }

        if (string.IsNullOrWhiteSpace(builder.DataSource))
        {
            throw new InvalidOperationException(
                "The configured database connection string names no server. " + KeyDescription);
        }

        if (string.IsNullOrWhiteSpace(builder.InitialCatalog))
        {
            throw new InvalidOperationException(
                "The configured database connection string names no database. This API maps onto an "
                + "existing DotNetNuke database and cannot select one for itself. " + KeyDescription);
        }

        if (!DeclaresAuthentication(builder))
        {
            throw new InvalidOperationException(
                "The configured database connection string names no way to authenticate. Supply "
                + "integrated security, an authentication method, or a user id together with a "
                + "password. " + KeyDescription);
        }

        return connectionString;
    }

    /// <summary>Reports whether a parsed connection string names a way to authenticate.</summary>
    /// <param name="builder">The parsed connection string.</param>
    /// <returns><see langword="true"/> when some authentication mechanism is declared.</returns>
    /// <remarks>
    /// Three mechanisms are accepted, and the breadth is deliberate: refusing a legitimate one would stop a
    /// deployment that is correctly configured, which is a worse failure than the one this method exists to
    /// catch.
    /// </remarks>
    private static bool DeclaresAuthentication(SqlConnectionStringBuilder builder)
    {
        if (builder.IntegratedSecurity)
        {
            return true;
        }

        if (builder.Authentication != SqlAuthenticationMethod.NotSpecified)
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(builder.UserID)
            && !string.IsNullOrWhiteSpace(builder.Password);
    }

    /// <summary>Reports whether any value in a parsed connection string is a documented placeholder.</summary>
    /// <param name="builder">The parsed connection string.</param>
    /// <returns><see langword="true"/> when a placeholder fragment appears in any value.</returns>
    private static bool ContainsPlaceholder(SqlConnectionStringBuilder builder)
    {
        string[] forbidden =
        [
            "change_me",
            "change-me",
            "changeme",
            "replace_me",
            "replace-me",
            "replaceme",
            "your-server",
            "your_server",
            "yourserver",
            "your-password",
            "your_password",
            "yourpassword",
            "placeholder",
            "todo",
            "xxxxx",
        ];

        foreach (object? value in builder.Values)
        {
            if (value?.ToString() is not string text || text.Length == 0)
            {
                continue;
            }

            foreach (string fragment in forbidden)
            {
                if (text.Contains(fragment, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>Registers the context and the unit of work.</summary>
    /// <param name="services">The collection to add to.</param>
    /// <param name="connectionString">The configured connection string.</param>
    /// <remarks>
    /// The alternative the framework offers is to wrap the whole transactional unit in the strategy's own
    /// <c>ExecuteAsync</c>, and it is rejected here for a concrete reason rather than a stylistic one.
    /// </remarks>
    private static void AddPersistence(IServiceCollection services, string connectionString)
    {
        services.AddDbContext<DnnDbContext>(options =>
            options.UseSqlServer(
                connectionString,
                sql =>
                {
                    sql.MigrationsHistoryTable(MigrationsHistoryTableName, MigrationsHistorySchema);

                    // Retry-on-failure is declared so that the retry count and delay are the provider's
                    // documented defaults rather than values invented here.
                    sql.EnableRetryOnFailure();

                    // ONE DECISION, DEFERRED TO EXECUTION TIME: retry when it is safe, do not when it is
                    // not. The substituted strategy keeps the provider's own retry policy and re-evaluates
                    // only whether retrying applies, each time it runs.
                    sql.ExecutionStrategy(dependencies => new TransactionAwareExecutionStrategy(dependencies));
                }));

        // The mapped tables among those now stage against one change tracker, and the sequence as a whole
        // is made durable by ITransactionScope.CommitAsync rather than by any single flush, because it
        // needs keys the store assigns during the first flush.
        services.AddScoped<IUnitOfWork, UnitOfWork>();

        // The collaborator that reaches the external ASP.NET membership tables. Scoped, because it works on
        // the context's own connection and enlists the context's transaction when one is open.
        services.AddScoped<MembershipStore>();

        services.AddSingleton<IStoreFailureClassifier, SqlStoreFailureClassifier>();
    }

    /// <summary>Registers the nine repositories.</summary>
    /// <param name="services">The collection to add to.</param>
    /// <remarks>
    /// All scoped, because each holds the scoped context and stages work the unit of work later commits. A
    /// singleton repository would share one change tracker between concurrent requests, which is how one
    /// tenant's pending edit ends up committed by another tenant's call.
    /// </remarks>
    private static void AddRepositories(IServiceCollection services)
    {
        services.AddScoped<IPortalRepository, PortalRepository>();
        services.AddScoped<IPortalAliasRepository, PortalAliasRepository>();
        services.AddScoped<ITabRepository, TabRepository>();
        services.AddScoped<IModuleRepository, ModuleRepository>();
        services.AddScoped<IModuleDefinitionRepository, ModuleDefinitionRepository>();
        services.AddScoped<IUserRepository, UserRepository>();
        services.AddScoped<IUserProfileRepository, UserProfileRepository>();
        services.AddScoped<IRoleRepository, RoleRepository>();
        services.AddScoped<IPermissionRepository, PermissionRepository>();
    }

    /// <summary>Registers the per-request tenant context and the holder that resolves it.</summary>
    /// <param name="services">The container being populated.</param>
    /// <remarks>
    /// Three registrations, and the shape of them matters.
    /// </remarks>
    private static void AddTenantContext(IServiceCollection services)
    {
        services.AddScoped<PortalContextHolder>();

        services.AddScoped<IPortalContextHolder>(provider =>
            provider.GetRequiredService<PortalContextHolder>());

        services.AddScoped<IPortalContext>(provider =>
            provider.GetRequiredService<PortalContextHolder>().Current);
    }

    /// <summary>Registers the security services.</summary>
    /// <param name="services">The collection to add to.</param>
    /// <param name="configuration">
    /// The host configuration used only to bind the migration-only legacy credential verifier.
    /// </param>
    /// <remarks>
    /// The password hasher and migration verifier are singletons because they hold only immutable
    /// configuration.
    /// </remarks>
    private static void AddSecurity(
        IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddScoped<IPermissionEvaluator, PermissionEvaluator>();
        services.AddSingleton<IPasswordHasher, BcryptPasswordHasher>();
        LegacyCredentialOptions legacyCredentialOptions = ReadLegacyCredentialOptions(configuration);
        services.AddSingleton<IOptions<LegacyCredentialOptions>>(
            Options.Create(legacyCredentialOptions));
        services.AddSingleton<ILegacyCredentialVerifier, LegacyCredentialVerifier>();

        AddRefreshTokenStore(services, configuration);

        services.AddSingleton<ITokenService, JwtTokenService>();
    }

    /// <summary>Registers the refresh-token store the configuration selects.</summary>
    /// <param name="services">The collection to add to.</param>
    /// <param name="configuration">The host configuration.</param>
    /// <remarks>
    /// TWO IMPLEMENTATIONS, ONE ABSTRACTION, AND THE CHOICE IS THE DEPLOYMENT'S. A refresh-token family is
    /// the only server-side session record this API keeps, so a revocation reaches exactly as far as the
    /// store does.
    /// </remarks>
    private static void AddRefreshTokenStore(
        IServiceCollection services,
        IConfiguration configuration)
    {
        RefreshTokenStoreOptions options = new();
        IConfigurationSection section = configuration.GetSection(RefreshTokenStoreOptions.SectionName);
        section.Bind(options);

        List<string> failures = [.. options.Validate(configuration.GetConnectionString("Default"))];

        // ⚠ A SETTING THAT NO LONGER EXISTS IS REFUSED RATHER THAN IGNORED. MIGRATION:
        // RefreshTokenStore:CreateTableIfMissing used to let the store issue CREATE TABLE under the API's
        // own identity; the table is now provisioned by a deployment step and the store only probes for it.
        if (section[RefreshTokenStoreOptions.RemovedCreateTableSetting] is not null)
        {
            failures.Add(
                FormattableString.Invariant(
                    $"'{RefreshTokenStoreOptions.SectionName}:{RefreshTokenStoreOptions.RemovedCreateTableSetting}' is set, and this build no longer has that setting.")
                + " The refresh-token table is provisioned by a deployment step rather than by the running"
                + " application, which never creates or alters it: run docker/sql/refresh-token-store.sql"
                + " against the configured session catalogue and remove the setting. It is refused rather than"
                + " ignored so that a deployment relying on runtime table creation is told, instead of"
                + " discovering it at the first sign-in.");
        }

        if (failures.Count != 0)
        {
            throw new OptionsValidationException(
                RefreshTokenStoreOptions.SectionName,
                typeof(RefreshTokenStoreOptions),
                failures);
        }

        services.AddSingleton<IOptions<RefreshTokenStoreOptions>>(Options.Create(options));

        services.AddHostedService<RefreshTokenRetentionService>();

        if (options.UsesSharedStore)
        {
            services.AddSingleton<IRefreshTokenStore, SqlServerRefreshTokenStore>();

            return;
        }

        services.AddSingleton<RefreshTokenStore>();

        // Resolved through the concrete registration above rather than registered against the
        // implementation type, so both service types share the one instance and the token families cannot
        // be duplicated.
        services.AddSingleton<IRefreshTokenStore>(
            provider => provider.GetRequiredService<RefreshTokenStore>());
    }

    /// <summary>Reads and validates the migration-only legacy credential settings.</summary>
    /// <param name="configuration">The host configuration.</param>
    /// <returns>The validated immutable-at-registration settings object.</returns>
    /// <exception cref="InvalidOperationException">A configured value is invalid.</exception>
    /// <remarks>
    /// The decryption key is never supplied by a tracked settings file. It is read only from the
    /// deployment's secret-backed configuration during the bounded migration window, copied into the
    /// verifier as key bytes, and removed after all legacy rows have been upgraded or reset.
    /// </remarks>
    private static LegacyCredentialOptions ReadLegacyCredentialOptions(IConfiguration configuration)
    {
        string enabledValue =
            configuration[$"{LegacyCredentialOptions.SectionName}:Enabled"] ?? bool.FalseString;
        if (!bool.TryParse(enabledValue, out bool enabled))
        {
            throw new InvalidOperationException(
                "LegacyCredentials:Enabled must be either true or false.");
        }

        string? deadlineValue =
            configuration[$"{LegacyCredentialOptions.SectionName}:EnabledUntilUtc"];
        DateTimeOffset? enabledUntilUtc = null;
        if (!string.IsNullOrWhiteSpace(deadlineValue))
        {
            if (!DateTimeOffset.TryParse(
                    deadlineValue,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out DateTimeOffset parsed))
            {
                throw new InvalidOperationException(
                    "LegacyCredentials:EnabledUntilUtc must be an ISO-8601 instant expressed in UTC.");
            }

            enabledUntilUtc = parsed;
        }

        LegacyCredentialOptions options = new()
        {
            Enabled = enabled,
            EnabledUntilUtc = enabledUntilUtc,
            DecryptionKey =
                configuration[$"{LegacyCredentialOptions.SectionName}:DecryptionKey"]
                ?? string.Empty,
            DecryptionAlgorithm =
                configuration[$"{LegacyCredentialOptions.SectionName}:DecryptionAlgorithm"]
                ?? "3DES",
            ValidationAlgorithm =
                configuration[$"{LegacyCredentialOptions.SectionName}:ValidationAlgorithm"]
                ?? "SHA1",
        };

        string? error = options.Validate();
        if (error is not null)
        {
            throw new InvalidOperationException(error);
        }

        return options;
    }

    /// <summary>Registers the platform services.</summary>
    /// <param name="services">The collection to add to.</param>
    /// <remarks>
    /// The cache service is a singleton over the framework memory cache, which is itself a singleton - a
    /// scoped cache would be discarded with the request and would therefore cache nothing.
    /// <c>AddMemoryCache</c> registers the store itself only if nothing has registered one already, and
    /// contributes its options as an ordinary configuration action, so a host that also calls it neither
    /// duplicates the store nor discards the size limit configured below: options actions accumulate rather
    /// than replace one another.
    /// </remarks>
    private static void AddPlatformServices(IServiceCollection services)
    {
        services.AddSingleton<IClock, SystemClock>();

        // A singleton, because it holds only a logger and a logger is thread-safe and lifetime-agnostic;
        // making it scoped would allocate one per request for no benefit.
        services.AddSingleton<ISecurityDiagnostics, SecurityDiagnostics>();
        services.AddSingleton<AuditPipelineHealth>();

        // The unit is entries, not bytes, because MemoryCacheService assigns every entry a size of one -
        // see the note there for why a byte estimate would be a fiction over object graphs.
        services.AddMemoryCache(options => options.SizeLimit = CacheEntryLimit);
        services.AddSingleton<ICacheService, MemoryCacheService>();

        services.AddScoped<IHostSettingsService, HostSettingsService>();

        // The audit trail is registered HERE, in the layer that owns the logging technology, against a
        // contract declared by the layer that owns the events.
        services.AddSingleton<IAuditSink, LoggingAuditSink>();

        services.AddScoped<IModuleBusinessControllerFactory, ModuleBusinessControllerFactory>();
    }

    /// <summary>Registers dependency and audit-delivery probes behind the health endpoint.</summary>
    /// <param name="services">The collection to add to.</param>
    /// <remarks>
    /// The database probe carries the readiness tag, and the tag is LOAD-BEARING rather than descriptive:
    /// the api layer maps its views with explicit predicates over it - a liveness view that excludes
    /// ready-tagged checks and a readiness view that selects them - so adding a dependency probe here
    /// without the tag silently moves it into the liveness view and makes the container's start-up depend
    /// on it.
    /// </remarks>
    private static void AddHealthChecks(IServiceCollection services)
    {
        services.AddScoped<DatabaseHealthCheck>();

        services.AddSingleton<RefreshTokenStoreHealth>();

        services
            .AddHealthChecks()
            .AddCheck<DatabaseHealthCheck>(
                DatabaseHealthCheckName,
                failureStatus: HealthStatus.Unhealthy,
                tags: new[] { ReadinessTag, DatabaseTag },
                timeout: ProbeTimeout)
            .AddCheck<AuditPipelineHealth>(
                AuditPipelineHealthCheckName,
                failureStatus: HealthStatus.Degraded,
                tags: new[] { LivenessTag, AuditTag })
            .AddCheck<RefreshTokenStoreHealth>(
                RefreshTokenStoreHealthCheckName,
                failureStatus: HealthStatus.Degraded,
                tags: new[] { LivenessTag, SessionStateTag });
    }

    /// <summary>
    /// Refuses to let the host finish starting when the declared refresh-token store and the store the
    /// container actually resolves are not the same thing.
    /// </summary>
    /// <param name="services">The built service provider, resolved from after the container is composed.</param>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// The configured provider and the registered implementation disagree, in either direction.
    /// </exception>
    /// <exception cref="OptionsValidationException">The store settings are invalid.</exception>
    /// <remarks>
    /// <strong>WHY IT IS AN EXPLICIT CALL RATHER THAN A HOSTED SERVICE OR AN OPTIONS VALIDATOR.</strong>
    /// The question is about the composed CONTAINER, not about a configuration value, so an
    /// <c>IValidateOptions&lt;T&gt;</c> cannot answer it: resolving <c>IRefreshTokenStore</c> from inside
    /// options validation would re-enter the very options creation that triggered the validation, because
    /// the store depends on these settings.
    /// </remarks>
    public static void ValidateRefreshTokenStoreTopology(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);

        RefreshTokenStoreOptions settings = services
            .GetRequiredService<IOptions<RefreshTokenStoreOptions>>()
            .Value;

        IReadOnlyList<string> failures = settings.Validate();
        if (failures.Count != 0)
        {
            throw new OptionsValidationException(
                RefreshTokenStoreOptions.SectionName,
                typeof(RefreshTokenStoreOptions),
                failures);
        }

        // Resolving the CONTRACT is the whole point: the container hands back the last registration of it,
        // so this is the instance the token service and the authentication service will use.
        IRefreshTokenStore activeStore = services.GetRequiredService<IRefreshTokenStore>();
        bool thisSolutionsStore = activeStore is RefreshTokenStore;

        if (settings.UsesExternalStore && thisSolutionsStore)
        {
            throw new InvalidOperationException(
                $"'{RefreshTokenStoreOptions.SectionName}:{nameof(RefreshTokenStoreOptions.Provider)}' is "
                + $"'{RefreshTokenStoreOptions.ExternalProvider}', which declares that this deployment "
                + "supplies its own refresh-token store, but the store the container resolves is this "
                + "solution's process-local one. Register the replacement AFTER AddInfrastructure - the last "
                + "registration of IRefreshTokenStore wins - or set the provider to "
                + $"'{RefreshTokenStoreOptions.InProcessProvider}' and accept that refresh state is neither "
                + "shared between replicas nor carried across a restart.");
        }

        if (settings.UsesSharedStore && thisSolutionsStore)
        {
            throw new InvalidOperationException(
                $"'{RefreshTokenStoreOptions.SectionName}:{nameof(RefreshTokenStoreOptions.Provider)}' is "
                + $"'{RefreshTokenStoreOptions.SqlServerProvider}', which selects this solution's shared, "
                + "durable store, but the store the container resolves is its process-local one. The shared "
                + "registration is conditional on the same setting, so this can only mean a later "
                + "registration of IRefreshTokenStore displaced it: remove that registration, or declare "
                + $"'{RefreshTokenStoreOptions.ExternalProvider}' so the configuration, the health report "
                + "and the operators all describe the store actually running.");
        }

        if (settings.UsesInProcessStore && !thisSolutionsStore)
        {
            throw new InvalidOperationException(
                $"'{RefreshTokenStoreOptions.SectionName}:{nameof(RefreshTokenStoreOptions.Provider)}' is "
                + $"'{RefreshTokenStoreOptions.InProcessProvider}', but an IRefreshTokenStore other than this "
                + "solution's process-local store is registered. Set the provider to "
                + $"'{RefreshTokenStoreOptions.ExternalProvider}' so that the deployment's configuration, its "
                + "health report and its operators all describe the store it is actually running.");
        }

        RequireSingleInstanceAcknowledgementInProduction(services, settings, activeStore);
    }

    /// <summary>
    /// Refuses to let a PRODUCTION host finish starting on a replica-local refresh-token store that the
    /// deployment has not acknowledged running.
    /// </summary>
    /// <param name="services">The built service provider, read for the hosting environment.</param>
    /// <param name="settings">The validated store settings.</param>
    /// <param name="activeStore">The store the container resolves.</param>
    /// <exception cref="InvalidOperationException">
    /// The environment is production, the active store is not authoritative across replicas, and the
    /// single-instance acknowledgement is absent.
    /// </exception>
    /// <remarks>
    /// <strong>THE PREDICATE IS THE CONTRACT'S OWN, NOT A TYPE TEST.</strong> <see
    /// cref="IRefreshTokenStore.IsAuthoritativeAcrossReplicas"/> is what decides this, so a
    /// deployment-supplied store that reports itself shared satisfies the invariant without this method
    /// knowing anything about it - and one that honestly reports itself replica-local is held to the same
    /// standard as this solution's own.
    /// </remarks>
    private static void RequireSingleInstanceAcknowledgementInProduction(
        IServiceProvider services,
        RefreshTokenStoreOptions settings,
        IRefreshTokenStore activeStore)
    {
        if (activeStore.IsAuthoritativeAcrossReplicas || settings.AcknowledgeSingleInstance)
        {
            return;
        }

        IHostEnvironment? environment = services.GetService<IHostEnvironment>();

        if (environment is null || !environment.IsProduction())
        {
            return;
        }

        throw new InvalidOperationException(
            "This deployment is running in Production on a refresh-token store that is NOT shared between "
            + "replicas and does not survive a restart, and it has not stated that it runs a single API "
            + "instance. Refresh-token families are the only server-side session record this API keeps, so a "
            + "sign-out performed against one instance leaves the session exchangeable on every other, and a "
            + "restart forgets every family it issued. Choose one of two: set "
            + $"'{RefreshTokenStoreOptions.SectionName}:{nameof(RefreshTokenStoreOptions.Provider)}' to "
            + $"'{RefreshTokenStoreOptions.SqlServerProvider}' with a "
            + $"'{RefreshTokenStoreOptions.SectionName}:{nameof(RefreshTokenStoreOptions.ConnectionString)}' "
            + "naming a session catalogue of its own, which makes sessions durable and shared - the table is "
            + "provisioned by running docker/sql/refresh-token-store.sql against that catalogue; or set "
            + $"'{RefreshTokenStoreOptions.SectionName}:{nameof(RefreshTokenStoreOptions.AcknowledgeSingleInstance)}' "
            + "to true, which changes no behaviour and records that exactly one instance is running. Set the "
            + "acknowledgement only where a single instance is genuinely enforced: it is refused as a default "
            + "precisely so that adding a replica later is a decision to revisit rather than a silent "
            + "regression.");
    }
}
