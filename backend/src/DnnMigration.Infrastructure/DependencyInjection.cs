// MIGRATION: this file is the composition root of the infrastructure layer and has no legacy
// counterpart. The legacy application declared fourteen provider families in configuration and
// activated each one by name - Library/Components/Providers/Data/DataProvider.vb:L31-L50 read a type,
// namespace and assembly out of Website/release.config and constructed it through reflection inside a
// static constructor, publishing the result through a Shared accessor. Three consequences followed, and
// all three are retired here. A misconfigured provider surfaced as a reflection failure on first use
// rather than at start-up. Nothing could be substituted, so nothing could be tested in isolation. And
// the lifetime of every provider was "one per application domain, forever", which is why so much legacy
// data-access code had to be static. Every dependency below is declared explicitly, with a lifetime
// chosen deliberately, and a missing registration now fails when the host is built.
//
// MIGRATION: three collaborators this layer could plausibly own are deliberately registered elsewhere,
// and the reasons are worth stating because their absence here looks like an omission.
//   * The four options classes are bound by the api layer. Binding a configuration section needs
//     Microsoft.Extensions.Options.ConfigurationExtensions, which this project does not reference and
//     must not: it would put configuration-shape knowledge in the layer that talks to the database.
//     This layer consumes the bound values through IOptions<T> and never reads a configuration key of
//     its own beyond the connection string it is handed.
//   * ICacheService, IClock and IHostSettingsService are registered here, as the platform section shows,
//     because their implementations are this layer's own and nothing above needs to name them.
//   * ICurrentUser is registered by the api layer, because it projects the claims of an authenticated
//     principal and there is no principal down here.
//
// MIGRATION: the per-request tenant snapshot IS registered here, in AddTenantContext, and not in the api
// layer. The holder that resolves it reads the PortalAlias table through this layer's own repository, so
// the resolution is a data-access concern; what the api layer owns is the decision of WHEN to resolve,
// which its alias-resolution middleware makes by calling the holder. Registering the snapshot here also
// keeps the implementation internal to this assembly, so nothing above can construct a tenant identity of
// its own - which is the failure mode worth foreclosing, because every authorisation decision trusts it.
using DnnMigration.Application.Abstractions;
using DnnMigration.Domain.Abstractions.Repositories;
using DnnMigration.Domain.Abstractions.Services;
using DnnMigration.Infrastructure.HealthChecks;
using DnnMigration.Infrastructure.Persistence;
using DnnMigration.Infrastructure.Repositories;
using DnnMigration.Infrastructure.Security;
using DnnMigration.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace DnnMigration.Infrastructure;

/// <summary>
/// Registers this layer's persistence, security and platform services with a service collection.
/// </summary>
public static class DependencyInjection
{
    /// <summary>The connection-string name this layer reads.</summary>
    /// <remarks>
    /// MIGRATION: the legacy name was <c>SiteSqlServer</c> (<c>Website/release.config:L21-L26</c>). The
    /// modern key is <c>ConnectionStrings:Default</c>, which a container overrides as the environment
    /// variable <c>ConnectionStrings__Default</c> - the exact form the compose file supplies. Published
    /// as a constant so the design-time factory, the host and any diagnostic all spell it once.
    /// </remarks>
    public const string ConnectionStringName = "Default";

    /// <summary>The name under which the entity-framework connectivity probe is registered.</summary>
    public const string DatabaseHealthCheckName = "database";

    /// <summary>The name under which the raw SQL Server connectivity probe is registered.</summary>
    public const string SqlServerHealthCheckName = "sqlserver";

    /// <summary>The schema holding the migrations-history table.</summary>
    /// <remarks>
    /// Must match the design-time factory exactly. The baseline migration exists only to seed a history
    /// row; if the runtime and the tooling disagreed about where that row lives, the tooling would
    /// conclude the baseline had never been applied and would try to apply it again.
    /// </remarks>
    private const string MigrationsHistorySchema = "dbo";

    /// <summary>The migrations-history table name.</summary>
    private const string MigrationsHistoryTableName = "__EFMigrationsHistory";

    /// <summary>Registers every service this layer implements.</summary>
    /// <param name="services">The collection to add to.</param>
    /// <param name="configuration">
    /// The host configuration, read only for the connection string. Nothing else is read from it here.
    /// </param>
    /// <returns>The same collection, so registration can be chained.</returns>
    /// <exception cref="ArgumentNullException">Either argument is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// No connection string is configured. Thrown while the host is being built rather than on the first
    /// request, because an API that starts and then fails every call is harder to diagnose than one that
    /// refuses to start.
    /// </exception>
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
        AddSecurity(services);
        AddPlatformServices(services);
        AddHealthChecks(services, connectionString);

        return services;
    }

    /// <summary>Registers one business controller under the name a module declares.</summary>
    /// <typeparam name="TController">
    /// The controller type. It becomes resolvable in its own right, so it may depend on scoped services
    /// such as a unit of work.
    /// </typeparam>
    /// <param name="services">The collection to add to.</param>
    /// <param name="businessControllerClass">
    /// The value stored in <c>Modules.BusinessControllerClass</c> that this controller answers to. Matched
    /// case-insensitively, because the stored column is free text that module manifests hand-entered.
    /// </param>
    /// <returns>The same collection, so registration can be chained.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="businessControllerClass"/> is blank.</exception>
    /// <remarks>
    /// <para>
    /// This is the only way a controller enters the map, and calling it is a code change - which is the
    /// whole point. MIGRATION: the legacy path treated a database column as an instruction to load an
    /// assembly and construct an arbitrary type by name, so a row a tenant administrator could edit
    /// decided which code ran in the server process. Here the set is closed before the first request and
    /// an unrecognised stored name resolves to nothing at all.
    /// </para>
    /// <para>
    /// The controller is registered scoped, not singleton. A business controller that touches module
    /// content needs the same unit of work as the request that invoked it, and the factory resolves it
    /// from a scope created for the call precisely so that works.
    /// </para>
    /// <para>
    /// The map itself is empty in this installation, and that is a finished state rather than a gap:
    /// every bundled module is out of scope for this migration, and most modules declare no business
    /// controller at all, so every lifecycle member correctly reports a successful no-op.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddModuleBusinessController<TController>(
        this IServiceCollection services,
        string businessControllerClass)
        where TController : class
    {
        ArgumentNullException.ThrowIfNull(services);

        if (string.IsNullOrWhiteSpace(businessControllerClass))
        {
            throw new ArgumentException(
                "A business-controller registration must carry a non-blank name.",
                nameof(businessControllerClass));
        }

        services.AddScoped<TController>();
        services.AddSingleton(
            new ModuleBusinessControllerRegistration(businessControllerClass, typeof(TController)));

        return services;
    }

    /// <summary>Reads the connection string, failing fast when it is absent.</summary>
    /// <param name="configuration">The host configuration.</param>
    /// <returns>The configured connection string.</returns>
    /// <exception cref="InvalidOperationException">No connection string is configured.</exception>
    /// <remarks>
    /// The message names the key in both its configuration and its environment-variable spelling, and
    /// never echoes a value: a connection string carries a password, so it must not reach a log, an
    /// exception message or a health-check response.
    /// </remarks>
    private static string ReadConnectionString(IConfiguration configuration)
    {
        string? connectionString = configuration.GetConnectionString(ConnectionStringName);

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "No database connection string is configured. Supply it as "
                + $"'ConnectionStrings:{ConnectionStringName}' in configuration, or as the environment "
                + $"variable 'ConnectionStrings__{ConnectionStringName}' in a container.");
        }

        return connectionString;
    }

    /// <summary>Registers the context and the unit of work.</summary>
    /// <param name="services">The collection to add to.</param>
    /// <param name="connectionString">The configured connection string.</param>
    /// <remarks>
    /// <para>
    /// The context is scoped, which is the framework default and the correct choice: its change tracker
    /// accumulates the edits of one request and the unit of work commits them together. Retry-on-failure
    /// is deliberately not enabled - the resulting execution strategy refuses to run inside a
    /// caller-opened transaction, and two collaborators in this layer enlist an open transaction when one
    /// exists. Turning it on would convert a working write path into a run-time failure that only appears
    /// once a transaction is actually opened, which is the worst possible time to discover it.
    /// </para>
    /// <para>
    /// No <c>EnsureCreated</c> and no automatic migration happens here, by rule. The schema is owned by
    /// the eighty-eight legacy upgrade scripts and depends on externally installed membership objects
    /// those scripts only alter, so the model cannot recreate it even in principle; the baseline
    /// migration exists purely to seed a history row.
    /// </para>
    /// </remarks>
    private static void AddPersistence(IServiceCollection services, string connectionString)
    {
        services.AddDbContext<DnnDbContext>(options =>
            options.UseSqlServer(
                connectionString,
                sql =>
                {
                    sql.MigrationsHistoryTable(MigrationsHistoryTableName, MigrationsHistorySchema);

                    // Transient faults are retried rather than surfaced. A managed SQL Server closes
                    // connections during a failover or a throttling episode, and the request that
                    // happened to hold one fails for a reason unrelated to anything the caller did.
                    // The strategy is safe here because nothing in this layer opens a user-initiated
                    // transaction - the unit of work commits through SaveChangesAsync, whose own
                    // transaction the strategy manages - and enabling it alongside one would throw.
                    sql.EnableRetryOnFailure();
                }));

        services.AddScoped<IUnitOfWork, UnitOfWork>();

        // The collaborator that reaches the external ASP.NET membership tables. Scoped, because it works
        // on the context's own connection and enlists the context's transaction when one is open. It is
        // registered as its concrete type rather than behind an interface deliberately: only this layer's
        // user repository may reach it, and giving it an abstraction would invite the application layer
        // to depend on a credential store directly.
        services.AddScoped<MembershipStore>();
    }

    /// <summary>Registers the nine repositories.</summary>
    /// <param name="services">The collection to add to.</param>
    /// <remarks>
    /// All scoped, because each holds the scoped context and stages work the unit of work later commits.
    /// A singleton repository would share one change tracker between concurrent requests, which is how
    /// one tenant's pending edit ends up committed by another tenant's call.
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

    /// <summary>
    /// Registers the per-request tenant context and the holder that resolves it.
    /// </summary>
    /// <param name="services">The container being populated.</param>
    /// <remarks>
    /// <para>
    /// Three registrations, and the shape of them matters. The holder is registered
    /// once as a concrete scoped service and then exposed through its abstraction by
    /// a factory that resolves that same instance, so a request has exactly one
    /// holder however it is reached; registering the abstraction separately would
    /// produce a second holder that resolves the tenant a second time and could
    /// disagree with the first.
    /// </para>
    /// <para>
    /// The third registration projects the resolved snapshot. It throws when the
    /// tenant has not been resolved yet, and that is the intended behaviour rather
    /// than an inconvenience: a component that reads a tenant before one is
    /// established has no correct value to be given, and inventing one is how
    /// cross-tenant defects begin. A component that may legitimately run before
    /// resolution takes the holder instead and asks whether a tenant is available.
    /// </para>
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
    /// <remarks>
    /// <para>
    /// The permission evaluator is a singleton because it is stateless: it is pure precedence arithmetic
    /// over grants a repository has already retrieved. It is registered as its concrete type on purpose.
    /// Allow-and-deny precedence is settled in exactly one place, and an interface here would be an
    /// invitation to supply a second implementation - two evaluators that can disagree being the worst
    /// available outcome in this area.
    /// </para>
    /// <para>
    /// The password hasher and the clock are singletons because they hold only configuration. The refresh
    /// token store is a singleton because it <em>is</em> the store: a scoped instance would lose every
    /// issued token at the end of the request that issued it. The token service is a singleton for the
    /// same reason and opens a scope per refresh to read entitlements, rather than capturing one.
    /// </para>
    /// </remarks>
    private static void AddSecurity(IServiceCollection services)
    {
        services.AddSingleton<PermissionEvaluator>();
        services.AddSingleton<IPasswordHasher, BcryptPasswordHasher>();
        services.AddSingleton<RefreshTokenStore>();

        // Resolved through the concrete registration above rather than registered against the
        // implementation type, so both service types share the one instance and the token families
        // cannot be duplicated. Registering the abstraction independently would give the token service
        // one store and anything resolving the abstraction a second, empty one - a refresh token issued
        // through the first would then be unknown to the second.
        services.AddSingleton<IRefreshTokenStore>(
            provider => provider.GetRequiredService<RefreshTokenStore>());

        services.AddSingleton<ITokenService, JwtTokenService>();
    }

    /// <summary>Registers the platform services.</summary>
    /// <param name="services">The collection to add to.</param>
    /// <remarks>
    /// <para>
    /// The clock is a singleton so that every time-dependent decision in the solution reads one source
    /// and a test can replace it wholesale. MIGRATION: the legacy code read <c>Date.Now</c> at each site,
    /// which is why none of that logic could be tested at a chosen instant.
    /// </para>
    /// <para>
    /// The cache service is a singleton over the framework memory cache, which is itself a singleton -
    /// a scoped cache would be discarded with the request and would therefore cache nothing.
    /// <c>AddMemoryCache</c> is idempotent, so calling it here does not conflict with a host that also
    /// calls it.
    /// </para>
    /// <para>
    /// The host-settings service is scoped because it reads through the scoped context. The module
    /// business-controller factory and its registry are singletons because the map is fixed at start-up;
    /// the factory nonetheless resolves each controller from a scope created for the call.
    /// </para>
    /// </remarks>
    private static void AddPlatformServices(IServiceCollection services)
    {
        services.AddSingleton<IClock, SystemClock>();

        services.AddMemoryCache();
        services.AddSingleton<ICacheService, MemoryCacheService>();

        services.AddScoped<IHostSettingsService, HostSettingsService>();

        services.AddSingleton<ModuleBusinessControllerRegistry>();
        services.AddSingleton<IModuleBusinessControllerFactory, ModuleBusinessControllerFactory>();
    }

    /// <summary>Registers the database probes behind the health endpoint.</summary>
    /// <param name="services">The collection to add to.</param>
    /// <param name="connectionString">The configured connection string.</param>
    /// <remarks>
    /// <para>
    /// Two probes of the same dependency, and the redundancy is intentional because they answer different
    /// questions. The first asks whether the <em>configured context</em> - the one every request persists
    /// through - can reach its store, so it covers provider configuration as well as connectivity. The
    /// second opens a raw connection from the same string and runs a trivial query, so it covers the
    /// string and the server without involving the model. Agreement is reassuring; disagreement localises
    /// a fault immediately to configuration rather than to the network.
    /// </para>
    /// <para>
    /// Both are tagged so a host can select between a readiness view and a liveness view. Both are
    /// registered as unhealthy-on-failure, which matters operationally: the api container's health check
    /// probes the health path, and the compose file makes the frontend's start-up conditional on the api
    /// reporting healthy, so a degraded status here would let the frontend start against an api that
    /// cannot serve a single request.
    /// </para>
    /// <para>
    /// The endpoint that exposes these must be anonymous. The container probe carries no credential, so a
    /// health path behind authentication would report the container unhealthy forever and the frontend
    /// would never start.
    /// </para>
    /// </remarks>
    private static void AddHealthChecks(IServiceCollection services, string connectionString)
    {
        services.AddScoped<DatabaseHealthCheck>();

        services
            .AddHealthChecks()
            .AddCheck<DatabaseHealthCheck>(
                DatabaseHealthCheckName,
                failureStatus: HealthStatus.Unhealthy,
                tags: new[] { "ready", "db" })
            .AddSqlServer(
                connectionString,
                name: SqlServerHealthCheckName,
                failureStatus: HealthStatus.Unhealthy,
                tags: new[] { "ready", "db" });
    }
}
