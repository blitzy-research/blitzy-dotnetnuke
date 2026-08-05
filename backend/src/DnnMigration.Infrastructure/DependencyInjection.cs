// MIGRATION: this file is the composition root of the infrastructure layer and has no legacy
// counterpart. The legacy application declared fourteen provider families in configuration and
// activated each one by name - Library/Components/Providers/Data/DataProvider.vb:L29-L50 read a type,
// namespace and assembly out of Website/release.config and constructed it through reflection inside a
// static constructor, publishing the result through a Shared accessor. Three consequences followed, and
// all three are retired here. A misconfigured provider surfaced as a reflection failure on first use
// rather than at start-up. Nothing could be substituted, so nothing could be tested in isolation. And
// the lifetime of every provider was "one per application domain, forever", which is why so much legacy
// data-access code had to be static. Every dependency below is declared explicitly, with a lifetime
// chosen deliberately, and a missing registration now fails when the host is built.
//
// MIGRATION: the reflection call and the Shared accessor are DELETED, NOT TRANSLATED. There is no
// Instance() equivalent anywhere in this solution and no service-locator of any kind: a collaborator
// arrives through a constructor or it does not arrive. Correspondingly, the fourteen defaultProvider
// declarations counted in Website/release.config do not survive as fourteen registrations. Provider
// indirection is REMOVED rather than REPRODUCED - what was configuration-selected late binding becomes
// either a single registration below or a typed IOptions<T> value bound by the api layer, so a setting
// that used to name a type now only carries data.
//
// MIGRATION: the 269 MustOverride members of that one abstract class - measured, in a 397-line file -
// are DECOMPOSED BY AGGREGATE BOUNDARY into the nine focused repository interfaces registered below,
// and ONLY THE IN-SCOPE SUBSET of those members is realised. The god-interface is not recreated as a
// nine-part copy of itself: each contract carries the operations of one aggregate, which is what makes
// a repository substitutable in a test and what stops an unrelated schema change rippling through an
// interface that every caller depends on. Members belonging to the excluded subsystems - scheduling,
// search, logging, caching and friendly-URL providers among them - are deliberately absent rather than
// stubbed, because a stub that silently succeeds is worse than a compile error.
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
using System.Globalization;
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
using Microsoft.Extensions.Options;

namespace DnnMigration.Infrastructure;

/// <summary>
/// Registers this layer's persistence, security and platform services with a service collection.
/// </summary>
public static class DependencyInjection
{
    /// <summary>The name under which the database connectivity probe is registered.</summary>
    /// <remarks>
    /// There is exactly ONE database probe, and this is its name. An earlier revision registered a second
    /// under the name <c>sqlserver</c>; the reasoning for withdrawing it is recorded at the registration
    /// below.
    /// </remarks>
    private const string DatabaseHealthCheckName = "database";

    /// <summary>The name under which failed audit delivery is surfaced.</summary>
    private const string AuditPipelineHealthCheckName = "audit-pipeline";

    /// <summary>
    /// Tag marking a probe as a READINESS signal rather than a liveness one.
    /// </summary>
    /// <remarks>
    /// The api layer maps its health views with explicit predicates over this tag, so the string is a
    /// contract between the two layers rather than a label. A probe registered without it joins the
    /// liveness view, which the container's start-up depends on.
    /// </remarks>
    private const string ReadinessTag = "ready";

    /// <summary>Tag naming the dependency a probe examines.</summary>
    private const string DatabaseTag = "db";

    /// <summary>Tag marking a probe as a process-local liveness signal.</summary>
    private const string LivenessTag = "live";

    /// <summary>Tag naming the accountability trail a probe examines.</summary>
    private const string AuditTag = "audit";

    /// <summary>
    /// Greatest time the dependency probe is allowed before the infrastructure abandons it.
    /// </summary>
    /// <remarks>
    /// Two seconds, so the probe can time out and the endpoint still answers inside the five seconds the
    /// container's own probe allows before it counts the attempt as a failure. A dependency that cannot be
    /// reached in two seconds is not one this service can serve requests through, so a longer bound would
    /// buy nothing but a slower answer - and an answer that arrives after the container has given up is no
    /// answer at all. The audit-delivery probe carries no timeout because it reaches nothing external: it
    /// answers from a process-local counter.
    /// </remarks>
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(2);
    /// <summary>The schema holding the migrations-history table.</summary>
    /// <remarks>
    /// Must match the design-time factory exactly. The baseline migration exists only to seed a history
    /// row; if the runtime and the tooling disagreed about where that row lives, the tooling would
    /// conclude the baseline had never been applied and would try to apply it again.
    /// </remarks>
    private const string MigrationsHistorySchema = "dbo";

    /// <summary>The migrations-history table name.</summary>
    private const string MigrationsHistoryTableName = "__EFMigrationsHistory";

    /// <summary>
    /// Largest number of entries the shared memory cache will hold before the runtime evicts the least
    /// recently used of them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A ceiling rather than a target, and expressed in ENTRIES because every entry this application writes
    /// declares a size of one. It exists so that the store's footprint is a property of the deployment
    /// rather than of the number of distinct keys the application happens to produce: without it, an entry
    /// is held until its own expiry and nothing bounds how many entries a caller-influenced key space can
    /// create within one lifetime.
    /// </para>
    /// <para>
    /// Fifty thousand is far above what this application caches. Its key families are dimensioned by
    /// tenant, page, module definition, account and profile-definition set, so an installation of any
    /// realistic size occupies a small fraction of the ceiling and no legitimate read is displaced; and
    /// eviction, when it does happen, is a re-read rather than an error, because every entry here is a
    /// read-through projection of data the store still holds.
    /// </para>
    /// </remarks>
    private const long CacheEntryLimit = 50_000;

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
        AddSecurity(services, configuration);
        AddPlatformServices(services);

        // Takes no connection string. The single probe registered there reads
        // ConnectionStrings:Default for itself, from the configuration the container supplies, so passing
        // the resolved value would only give the probe a second, earlier-captured copy of what it already
        // reads - and it is deliberately the probe rather than this method that decides how an absent value
        // is reported, because absence is a health RESULT and not a start-up failure.
        AddHealthChecks(services);

        return services;
    }

    /// <summary>Reads the connection string, failing fast when it is absent.</summary>
    /// <param name="configuration">The host configuration.</param>
    /// <returns>The configured connection string.</returns>
    /// <exception cref="InvalidOperationException">No connection string is configured.</exception>
    /// <remarks>
    /// <para>
    /// MIGRATION: the legacy name was <c>SiteSqlServer</c>, declared twice as a connection string and
    /// twice again as an appSetting in <c>Website/release.config</c> (L24, L29, L36, L38). The modern key
    /// is <c>ConnectionStrings:Default</c>, which a container overrides as the environment variable
    /// <c>ConnectionStrings__Default</c> - the exact form the compose file supplies from
    /// <c>DB_CONNECTION_STRING</c>. This is the ONLY configuration key this layer reads; everything else
    /// it needs arrives as a bound <c>IOptions&lt;T&gt;</c> value, so configuration-shape knowledge stays
    /// in the layer that owns configuration.
    /// </para>
    /// <para>
    /// The message names the key in both its configuration and its environment-variable spelling, and
    /// never echoes a value: a connection string carries a password, so it must not reach a log, an
    /// exception message or a health-check response.
    /// </para>
    /// </remarks>
    private static string ReadConnectionString(IConfiguration configuration)
    {
        string? connectionString = configuration.GetConnectionString("Default");

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "No database connection string is configured. Supply it as "
                + "'ConnectionStrings:Default' in configuration, or as the environment "
                + "variable 'ConnectionStrings__Default' in a container.");
        }

        return connectionString;
    }

    /// <summary>Registers the context and the unit of work.</summary>
    /// <param name="services">The collection to add to.</param>
    /// <param name="connectionString">The configured connection string.</param>
    /// <remarks>
    /// <para>
    /// The context is scoped, which is the framework default and the correct choice: its change tracker
    /// accumulates the edits of one request and the unit of work flushes them together.
    /// </para>
    /// <para>
    /// RETRY-ON-FAILURE IS ENABLED, BUT SUSPENDED INSIDE A CALLER-OPENED TRANSACTION, and this is the one
    /// arrangement in which both properties can be had at once. A retrying execution strategy refuses to
    /// run while a user-initiated transaction is open - <c>SaveChangesAsync</c> throws
    /// "the configured execution strategy does not support user-initiated transactions" - and two write
    /// paths in this layer open one, because creating and removing a tenant each span more than one flush
    /// and must be atomic.
    /// </para>
    /// <para>
    /// The alternative the framework offers is to wrap the whole transactional unit in the strategy's own
    /// <c>ExecuteAsync</c>, and it is rejected here for a concrete reason rather than a stylistic one. A
    /// retry re-invokes the delegate, but a rolled-back transaction does NOT reset the change tracker: the
    /// entities the first attempt saved are left tracked as unchanged, holding store-assigned keys for rows
    /// that no longer exist. A replay would therefore issue updates against missing rows or insert
    /// duplicates, which is a data-integrity fault in place of a transient one - strictly worse than the
    /// failure it was meant to absorb.
    /// </para>
    /// <para>
    /// Deciding at strategy-creation time gives both. Outside a transaction, a transient fault is retried
    /// exactly as before: a managed SQL Server closes connections during a failover or a throttling episode
    /// and the request that happened to hold one fails for a reason unrelated to anything the caller did.
    /// Inside a transaction, retrying is suspended, so the strategy raises no objection and the ATOMICITY
    /// provides the resilience instead - the whole unit is reversed and the caller retries the operation
    /// rather than the framework replaying half of it.
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

                    // Retry-on-failure is declared so that the retry count and delay are the provider's
                    // documented defaults rather than values invented here. The factory below decides
                    // which strategy is actually used, so this call establishes the policy and the
                    // factory establishes when it applies.
                    sql.EnableRetryOnFailure();

                    // ONE DECISION, DEFERRED TO EXECUTION TIME: retry when it is safe, do not when it is
                    // not. The substituted strategy keeps the provider's own retry policy and re-evaluates
                    // only whether retrying applies, each time it runs. Deciding here - in the factory -
                    // cannot work, because the strategy is resolved once per context scope and is built
                    // before any transaction opens. TransactionAwareExecutionStrategy documents the
                    // reasoning in full.
                    sql.ExecutionStrategy(dependencies => new TransactionAwareExecutionStrategy(dependencies));
                }));

        // MIGRATION: registering this gives the write paths a flush boundary and an explicit-transaction
        // boundary where the legacy code had neither. The case that proves the need is CreatePortal at
        // Library/Components/Portal/PortalController.vb:L980 - fifteen positional parameters - which writes
        // across SEVEN tables in sequence: Portals (through the two-argument CreatePortal overload), then
        // ProfilePropertyDefinition (CreateProfileDefinitions), then Tabs, Modules and Roles
        // (ParseTemplate), then Users (UserController.UpdateUser at :L1126), and finally PortalAlias
        // (AddPortalAlias at :L1134). NO TRANSACTION SPANS ANY OF IT: searching that entire 1,632-line file
        // for a transaction of any kind returns nothing, so a failure at the fourth write left a portal with
        // no administrator and no alias - reachable by nobody, deletable through no screen.
        //
        // The mapped tables among those now stage against one change tracker, and the sequence as a whole is
        // made durable by ITransactionScope.CommitAsync rather than by any single flush, because it needs
        // keys the store assigns during the first flush. The credential is the exception and is worth stating
        // plainly: it lives in the external aspnet_* membership tables, which no entity type maps, so
        // MembershipStore writes it with direct SQL. That write enlists the context's transaction when one is
        // open - which is how the sequence stays atomic - but it is not staged in the change tracker and no
        // flush reverses it.
        //
        // The legacy code also cleared caches mid-sequence, at :L1128 DataCache.ClearHostCache(True) and
        // :L1131 DataCache.RemoveCache("GetRoles") - that is, BEFORE the alias write it depended on had
        // happened. Cache invalidation now follows a successful commit rather than racing it.
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
    /// <param name="configuration">
    /// The host configuration used only to bind the migration-only legacy credential verifier.
    /// </param>
    /// <remarks>
    /// <para>
    /// MIGRATION: the permission evaluator is registered per request, not as a singleton. Its precedence
    /// arithmetic is stateless, but it now also resolves the grants that arithmetic runs over - which is
    /// where AAP section 0.4.3 places evaluation - so it depends on the unit-of-work scoped database
    /// context and must share that context's lifetime. The permission repository no longer performs any
    /// part of evaluation: its Domain contract mirrors the legacy provider blocks at core
    /// <c>DataProvider.vb</c> L279-L308 and is pure persistence.
    /// </para>
    /// <para>
    /// It is registered against <see cref="IPermissionEvaluator"/> because the application layer has to
    /// reach it without seeing the store, and that abstraction exists for no other purpose. Exactly one
    /// implementation is registered against it, deliberately: allow-and-deny precedence is settled in one
    /// place, two evaluators that can disagree being the worst available outcome in this area.
    /// </para>
    /// <para>
    /// The password hasher and migration verifier are singletons because they hold only immutable
    /// configuration. The refresh-token store and token service are scoped because the store performs
    /// durable SQL I/O through the request's configured database boundary; its state lives in SQL, not in
    /// the service instance, so no session is lost when the scope ends.
    /// </para>
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
        services.AddScoped<RefreshTokenStore>();

        // Resolved through the concrete registration above rather than registered against the
        // implementation type, so both service types share the one instance and the token families
        // cannot be duplicated. Registering the abstraction independently would give the token service
        // one store and anything resolving the abstraction a second, empty one - a refresh token issued
        // through the first would then be unknown to the second.
        services.AddScoped<IRefreshTokenStore>(
            provider => provider.GetRequiredService<RefreshTokenStore>());

        services.AddScoped<ITokenService, JwtTokenService>();
    }

    /// <summary>Reads and validates the migration-only legacy credential settings.</summary>
    /// <param name="configuration">The host configuration.</param>
    /// <returns>The validated immutable-at-registration settings object.</returns>
    /// <exception cref="InvalidOperationException">A configured value is invalid.</exception>
    /// <remarks>
    /// MIGRATION: the decryption key is never supplied by a tracked settings file. It is read only
    /// from the deployment's secret-backed configuration during the bounded migration window, copied
    /// into the verifier as key bytes, and removed after all legacy rows have been upgraded or reset.
    /// No validation error echoes any part of the value.
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
            // Round-trip parsing only, so that an offset the operator wrote is preserved rather than
            // reinterpreted: the options validator refuses anything other than a zero offset, and it can
            // only do that if parsing has not already assumed one. A value that is not a timestamp at all
            // is refused here rather than being treated as an absent deadline, because silently ignoring
            // it would leave the switch on with no window at all.
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
    /// <para>
    /// The clock is a singleton so that every time-dependent decision in the solution reads one source
    /// and a test can replace it wholesale. MIGRATION: the legacy code read <c>Date.Now</c> at each site,
    /// which is why none of that logic could be tested at a chosen instant.
    /// </para>
    /// <para>
    /// The cache service is a singleton over the framework memory cache, which is itself a singleton -
    /// a scoped cache would be discarded with the request and would therefore cache nothing.
    /// <c>AddMemoryCache</c> registers the store itself only if nothing has registered one already, and
    /// contributes its options as an ordinary configuration action, so a host that also calls it neither
    /// duplicates the store nor discards the size limit configured below: options actions accumulate rather
    /// than replace one another. A host that deliberately configured its own limit would be the last action
    /// to run and would win, which is the correct precedence for a host overriding a library default.
    /// </para>
    /// <para>
    /// The host-settings service is scoped because it reads through the scoped context. The module
    /// business-controller factory is scoped as well, so any controller it resolves shares the caller's
    /// request scope and unit of work. The set of controller types is fixed at start-up; its immutability
    /// does not require the factory that consumes a request scope to be a singleton.
    /// </para>
    /// <para>
    /// The audit sink is a singleton because it holds no per-request state - every fact it needs arrives
    /// on the event - and because it must be resolvable from a service whose own lifetime may be longer
    /// than a request. Its process-local health collaborator is also a singleton, so failed-delivery
    /// counts are shared across every request and exposed by one health-check registration. MIGRATION: the
    /// sink is registered in this layer rather than the api layer even though what it writes to is the
    /// logging pipeline, for the same reason the cache and the clock are: the application layer names only
    /// the abstraction, and the concrete logging dependency belongs on this side of the boundary. Nothing
    /// above can construct one, so no caller can bypass the sink's never-throw guarantee with an
    /// implementation of its own.
    /// </para>
    /// </remarks>
    private static void AddPlatformServices(IServiceCollection services)
    {
        services.AddSingleton<IClock, SystemClock>();

        // A singleton, because it holds only a logger and a logger is thread-safe and lifetime-agnostic;
        // making it scoped would allocate one per request for no benefit. It is registered here rather than
        // beside the security services because it is a platform capability that layers above cannot reach for
        // themselves: the Application layer's package surface excludes every logging assembly, so this
        // registration is the only route by which a service in that layer can report an anomaly at all. A host
        // that omits it leaves those services unconstructable, which is the intended failure - a missing
        // diagnostic route must be a start-up error rather than silence.
        services.AddSingleton<ISecurityDiagnostics, SecurityDiagnostics>();
        services.AddSingleton<AuditPipelineHealth>();

        // BOUNDED, AND THE BOUND IS THE POINT OF CONFIGURING IT AT ALL. The framework cache is unlimited by
        // default: every entry written to it is held until its own expiry, so the store's size is decided by
        // how many distinct keys the application happens to produce rather than by anything the deployment
        // controls. A size limit turns that into a ceiling the runtime enforces by evicting the least
        // recently used entries when it is approached, which converts a cache-cardinality defect anywhere in
        // the application from unbounded memory growth into ordinary cache pressure.
        //
        // The unit is entries, not bytes, because MemoryCacheService assigns every entry a size of one - see
        // the note there for why a byte estimate would be a fiction over object graphs. The figure is
        // generous against what this application actually caches: the portal, page, module, account and
        // permission families are keyed by tenant, page, module definition or account, so an installation of
        // ordinary size occupies a small fraction of it and never sees an eviction that expiry would not
        // have performed anyway.
        services.AddMemoryCache(options => options.SizeLimit = CacheEntryLimit);
        services.AddSingleton<ICacheService, MemoryCacheService>();

        services.AddScoped<IHostSettingsService, HostSettingsService>();

        // The audit trail is registered HERE, in the layer that owns the logging technology, against a
        // contract declared by the layer that owns the events. There is exactly ONE audit abstraction, and
        // deliberately so: two would let one service's events be captured while another's were not, and an
        // audit trail that is only sometimes complete is worse than one that is uniformly incomplete. That split is not stylistic: the
        // application project declares FluentValidation and nothing else, so it cannot name a logger, and
        // an audit abstraction is the only way its services can emit a business event at all. A singleton
        // because its logger, diagnostics route and health counter are all singleton, thread-safe platform
        // services and it holds no request state.
        services.AddSingleton<IAuditSink, LoggingAuditSink>();

        // Scoped, and deliberately not a singleton: the factory resolves each controller from the CALLER's
        // scope, so a lifecycle operation shares the request's database context and therefore its unit of
        // work. A singleton factory holding the root provider would resolve controllers outside the request
        // scope, and content a controller wrote would then commit independently of the caller's transaction.
        //
        // MIGRATION: the factory resolves from a CLOSED set, and that set is EMPTY in this installation -
        // a finished state rather than a gap. The legacy path treated Modules.BusinessControllerClass, a
        // database column, as an instruction to load an assembly and construct an arbitrary type by name
        // (Framework.Reflection.CreateObject at Library/Components/Modules/ModuleController.vb:L231,L431
        // and EventMessageProcessor.vb:L32,L52,L77), so a row an administrator could edit decided which
        // code ran in the server process. Every bundled module is out of scope for this migration per AAP
        // 0.2.2.2 and most declared no controller at all, so an unrecognised stored name resolves to
        // nothing. Admitting a controller is therefore a CODE change, not a data change - which is the
        // whole point, and is why no public registration hook is published here for a caller to widen the
        // set at run time.
        //
        // WHAT AN EMPTY SET MEANS TO A CALLER, precisely, because getting this wrong produced a real
        // defect. The read members - capability probe and export - report absence as a successful answer
        // whose VALUE says nothing was found, so absence is already distinguishable from a result. The
        // IMPORT member does not have that luxury: its success carries no value, and its caller commits a
        // unit of work, evicts caches and writes an audit record on the strength of it. Reporting absence
        // there as a successful no-op therefore told the caller 200 and told the audit trail that content
        // had been imported into a module the installation cannot even ask. Import consequently REFUSES
        // every state in which the controller was not asked, each with its own code, so an empty set makes
        // content import unavailable rather than silently vacuous.
        //
        // HOW A CONTROLLER IS ADMITTED, since the set being empty must not be mistaken for the mechanism
        // being absent. The factory resolves each controller as a KEYED service off the caller's own scope,
        // keyed by the trimmed, lower-cased stored class name, so a registration of the form
        // services.AddKeyedScoped<object, MyModuleController>("mymodule.controller") is all that is needed
        // and is exercised end to end by the test host. Nothing is registered here because there is no
        // in-scope module to register, and inventing one would ship dead production code.
        services.AddScoped<IModuleBusinessControllerFactory, ModuleBusinessControllerFactory>();
    }

    /// <summary>Registers dependency and audit-delivery probes behind the health endpoint.</summary>
    /// <param name="services">The collection to add to.</param>
    /// <remarks>
    /// <para>
    /// ONE PROBE OF ONE DEPENDENCY. An earlier revision registered two - this check plus the package's own
    /// SQL Server check - on the argument that they answered different questions, and that argument does
    /// not survive inspection of what each actually does: both read the same
    /// <c>ConnectionStrings:Default</c> value and both open a connection to the instance it names, so the
    /// second could disagree with the first only by being flaky. Neither exercises the entity model, so
    /// neither localises a fault to provider configuration. The cost of the redundancy was two connections
    /// for every probe of a path that is polled continuously - the api image declares a <c>HEALTHCHECK</c>
    /// against it, the compose file gates the frontend's start-up on it, and an orchestrator re-probes it
    /// for the life of the container - so the duplicate is withdrawn and the cheaper check kept. Whether
    /// the model agrees with the schema it maps is settled by the integration suite, which is the right
    /// place for it.
    /// </para>
    /// <para>
    /// The database probe carries the readiness tag, and the tag is LOAD-BEARING rather than descriptive:
    /// the api layer maps its views with explicit predicates over it - a liveness view that excludes
    /// ready-tagged checks and a readiness view that selects them - so adding a dependency probe here
    /// without the tag silently moves it into the liveness view and makes the container's start-up depend
    /// on it. It is registered as unhealthy-on-failure, which matters operationally: a degraded status
    /// maps to 200, so it would let a caller conclude the api can serve requests it cannot.
    /// </para>
    /// <para>
    /// The second check is the process-local audit-delivery counter, and it is the one probe that belongs in
    /// the LIVENESS view: it reaches nothing external, so it cannot hold the container back at start-up. It
    /// reports degraded after any lost audit record, and degraded intentionally remains an HTTP-successful
    /// health response because the API can still serve requests; the named check tells operators its
    /// accountability trail is incomplete without taking the application down after a completed mutation.
    /// </para>
    /// <para>
    /// Every view must be anonymous. The container probe carries no credential, so a health path behind
    /// authentication would report the container unhealthy forever and the frontend would never start.
    /// </para>
    /// <para>
    /// <strong>The dependency probe is bounded by its own timeout.</strong> Without one, a probe inherits
    /// only the connection timeout inside the configured string - which a deployment may set to anything,
    /// and which the default of fifteen seconds already exceeds by a wide margin. The container's own probe
    /// abandons its request after five seconds and counts that as a failure, so an unbounded probe turns a
    /// slow dependency into an unexplained unhealthy container: the check never answers, so nothing says
    /// why. The value below is chosen so that the probe can time out and the endpoint still answer inside
    /// that budget, and a probe that times out is reported as a timeout rather than as an outage, because
    /// the check rethrows cancellation instead of converting it.
    /// </para>
    /// </remarks>
    private static void AddHealthChecks(IServiceCollection services)
    {
        services.AddScoped<DatabaseHealthCheck>();

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
                tags: new[] { LivenessTag, AuditTag });
    }
}
