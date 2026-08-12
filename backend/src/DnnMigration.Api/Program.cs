using DnnMigration.Api.ErrorHandling;
using DnnMigration.Api.Extensions;
using DnnMigration.Api.Logging;
using DnnMigration.Application;
using DnnMigration.Infrastructure;
using Serilog;
using Serilog.Formatting.Compact;

// The composition root, and nothing else. Every registration lives behind one of the extension methods below
// and every pipeline stage behind UseApiPipeline, so the shape of the application is readable in one screen:
// configure logging, register, compose, run.
//
// MIGRATION: the legacy managed-module chain and page lifecycle are replaced by the explicit ordered stages
// mapped below rather than translated, and several responsibilities that lifecycle carried are deliberately
// not ported because the subsystem behind each is excluded: client cache policy and security headers now
// belong to the reverse proxy, skin, stylesheet and favicon handling to the single-page application, and
// affiliate tracking and site logging to subsystems this migration does not carry. MIGRATION_NOTES.md
// records each omission individually.

// A bootstrap logger, established before anything else can fail and deliberately unconfigurable, because
// configuration has not been read yet. The startup guards below fail closed on a weak signing key, an
// unbounded token lifetime, an unusable portal path or a missing connection string, and a fail-closed
// startup that emits nothing is a container that exits with no explanation. Replaced by the configured
// logger below.
Log.Logger = new LoggerConfiguration()
    .WriteTo.Console(new CompactJsonFormatter())
    .CreateBootstrapLogger();

try
{
    WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

    // FILE-DELIVERED SECRETS, AS A REGISTERED CONFIGURATION SOURCE RATHER THAN AS A PROMISE.
    //
    // Every deployment value this API reads is an ordinary configuration key, so it does not care which
    // provider supplies it - but a provider has to be REGISTERED before that statement is true of files.
    // docker/.env.example told operators that Docker Swarm, Kubernetes or a managed secret store could
    // deliver the connection string and the signing key as mounted files "without any code change", and no
    // key-per-file source was registered anywhere: a /run/secrets mount was simply not read. The template
    // described a capability the host did not have, and this is the half that was missing.
    //
    // ONE FILE PER KEY, AND THE FILE NAME IS THE KEY. The provider reads every file in the directory and
    // uses its NAME as the configuration key, translating a double underscore into the section separator
    // exactly as an environment variable does - so a file called `Jwt__Secret` supplies `Jwt:Secret` and
    // `ConnectionStrings__Default` supplies the connection string. That is precisely the shape
    // `docker compose` produces for `secrets:` (which mounts each secret at /run/secrets/<target>), the
    // shape a Kubernetes secret volume produces, and the shape an entrypoint or a managed-secret agent can
    // write into an emptyDir. The two names above are the only ones this API requires from a secret store.
    //
    // PRECEDENCE IS LAST, AND THEREFORE HIGHEST. The builder's own sources are appsettings.json, the
    // environment overlay, user secrets in Development, environment variables and command-line arguments,
    // in that order; adding this one after them means a mounted secret wins over an environment variable
    // of the same name. That is the correct direction for this application: a deployment that has gone to
    // the trouble of mounting a secret has done so precisely to stop the value appearing in an environment
    // block that `docker inspect` can read, and an inherited environment variable must not silently defeat
    // it. It also makes the migration path off environment delivery a one-step change - mount the file,
    // remove the variable when convenient - rather than a cutover.
    //
    // OPTIONAL, AND SILENT WHEN ABSENT. `optional: true` is what lets the same image run in the base
    // compose topology, in the test host and on a workstation, none of which mount anything: an absent
    // directory contributes no keys and no error. The path is itself configurable - from a source that has
    // already been read, i.e. an environment variable or an appsettings entry - because Kubernetes
    // projected volumes are conventionally mounted elsewhere and a deployment must not have to rebuild the
    // image to say so. A blank value is treated as "not configured" rather than as the filesystem root,
    // which is the one reading of an empty path that could load something unintended.
    //
    // NO SECRET IS LOGGED BY THIS. The provider reports nothing; the keys it supplies are consumed by the
    // same validated options as before, and those validators are written never to echo a value.
    string secretsDirectory =
        builder.Configuration[ServiceCollectionExtensions.SecretsDirectorySectionName]
                is { Length: > 0 } configuredSecretsDirectory
            ? configuredSecretsDirectory
            : ServiceCollectionExtensions.DefaultSecretsDirectory;

    builder.Configuration.AddKeyPerFile(secretsDirectory, optional: true);

    // Replaces the bootstrap logger. Sinks and levels come from configuration so a deployment can change
    // them without a rebuild; reading from the container as well is what allows a sink to resolve a
    // registered service.
    //
    // MIGRATION: the legacy typed audit-log entries are now structured Serilog events emitted from the
    // application services, which is why the minimum level for this application's own namespaces must never
    // be raised above Information - that would discard the audit trail rather than the noise. Nothing here
    // captures a request body, and the EF Core database-command category stays at Warning in every
    // appsettings overlay, because at Information it logs SQL parameter values.
    builder.Host.UseSerilog((context, services, logger) => logger
        .ReadFrom.Configuration(context.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext()

        // Runs after the logging-scope properties are attached, the only position from which one of them can
        // be taken back off: the hosting layer scopes the raw request path over every entry written while a
        // request is handled, so removing the path from one message template would leave the same value on
        // the same entry under a different name. SensitiveLogPropertyScrubber records what it removes and
        // why.
        .Enrich.With(new SensitiveLogPropertyScrubber()));

    // Order matters in exactly one place: the API layer's own registration runs first because it registers
    // the controller services, and the versioned API explorer configured by the documentation registration
    // must be applied on top of those. The remaining calls are independent of one another.
    //
    // AddApiServices owns the JSON contract, and the legacy null encoding constrains it: -1, 0, the empty
    // string and false are each legitimate persisted values - the schema seeds Portals.PortalID at
    // IDENTITY(-1,1) and Roles.RoleID, Tabs.TabID and Modules.ModuleID at IDENTITY(0,1), and the legacy role
    // vocabulary reserves "-1" through "-4" - so the serializer is configured with DefaultIgnoreCondition =
    // JsonIgnoreCondition.Never and must never be switched to a condition that omits nulls or default
    // values, which would erase a legitimate identifier from every response. Property names are camelCase to
    // match the SPA's core/models/*.model.ts interfaces.
    //
    // MIGRATION: a bearer token is self-contained and cannot be recalled the way the legacy
    // forms-authentication ticket could, so POST /api/v1/auth/logout revokes the refresh token and otherwise
    // relies on the short access-token lifetime plus client-side discard. AddCredentialRateLimiting is the
    // compensating control for the excluded CAPTCHA gate: the brute-force defence it provided is now a
    // request budget on the credential endpoints.
    builder.Services
        .AddApiServices(builder.Configuration)
        .AddApplication()
        .AddInfrastructure(builder.Configuration)
        .AddSpaCors(builder.Configuration)
        .AddJwtAuthentication(builder.Configuration)
        .AddAuthorizationPolicies()
        .AddCredentialRateLimiting(builder.Configuration)
        .AddSwaggerDocumentation();

    WebApplication app = builder.Build();

    // THE COMPOSED GRAPH IS INSPECTED BEFORE ANYTHING IS SERVED, for one question that only the built
    // container can answer: does the refresh-token store this deployment DECLARES match the store the
    // container actually resolves?
    //
    // The store this solution ships is process-local. AAP rule T4 forbids adding a table to the existing
    // DotNetNuke schema, AAP 0.6 freezes a dependency inventory that contains no distributed-cache client, and
    // AAP 0.9.3 reproduces a two-service container topology verbatim, so refresh state lives in this process:
    // it is not shared between replicas and does not survive a restart. That is documented in
    // MIGRATION_NOTES.md and in README.md, and IRefreshTokenStore is a public contract a deployment may
    // replace - it registers its own implementation after AddInfrastructure, since the last registration of a
    // service wins, and sets RefreshTokenStore:Provider to "External".
    //
    // The call below is what stops that arrangement failing silently in either direction: declaring an
    // external store while registering none would scale out on top of the process-local store, and registering
    // a replacement while still declaring "InProcess" would leave the configuration, the health report and the
    // operators describing a store the process is not running. Both are start-up failures rather than
    // discoveries made when a user loses a session.
    //
    // It is an explicit call here rather than a hosted service or an options validator, for the reasons argued
    // at ValidateRefreshTokenStoreTopology: an options validator would re-enter the options creation it was
    // triggered by, and this solution registers no hosted service that such an assertion could join. Placed
    // before UseApiPipeline so that a refusal happens while the host is still starting, and it also forces the
    // store's own settings to be validated eagerly rather than on the first sign-in.
    //
    // Fully qualified deliberately: the Application and Infrastructure layers each publish a static
    // DependencyInjection class and both namespaces are imported above, so the short name is ambiguous.
    DnnMigration.Infrastructure.DependencyInjection.ValidateRefreshTokenStoreTopology(app.Services);

    // The pipeline. Its order is fixed and non-negotiable, and it is mapped here as well as at its definition
    // so that a reordering shows up as a contradiction between two files rather than passing unnoticed in one:
    //
    //   1. exception handler        UseExceptionHandler()
    //   2. correlation id           CorrelationIdMiddleware
    //   3. request logging          RequestLoggingMiddleware
    //   4. routing                  UseRouting()
    //   5. CORS                     UseCors(named policy)
    //   6. authentication           UseAuthentication()
    //   7. portal-alias resolution  PortalAliasResolutionMiddleware
    //   8. authorization            UseAuthorization()
    //   9. controllers              MapControllers()
    //  10. health checks            MapHealthChecks("/health")        liveness, the probed one
    //                               MapHealthChecks("/health/ready")  readiness
    //                               MapHealthChecks("/health/live")   bare liveness
    //
    // Extensions/ApplicationBuilderExtensions.cs argues every position and interleaves five further stages,
    // none of which displaces a numbered one. Two consequences belong here because both reach outside the
    // pipeline.
    //
    // Stage 10 is ANONYMOUS AND MUST REMAIN SO, and the path the deployment artefacts probe is "/health": the
    // image's HEALTHCHECK reads it with wget before any credential exists, and the front-end service is held
    // back by "condition: service_healthy" until it answers, so requiring authorisation there would stop the
    // deployment rather than secure it. "/health" is the LIVENESS view and deliberately excludes the
    // ready-tagged database probe, so a starting container is not held back by an external store the compose
    // topology does not declare.
    //
    // Stages 7 and 8 are in that order deliberately, where AAP 0.5.1.4 has them reversed. The plan assumed one
    // portal-alias stage; the implementation needs two, because rewriting the path base for a child portal must
    // precede routing while deciding whether an endpoint requires a tenant must follow it. Once split, the
    // refusal cannot sit behind authorisation: a tenant-scoped policy reconciles the caller's portal against
    // the ARRIVAL portal, and behind authorisation those policies would be evaluated with no arrival portal at
    // all. The divergence is deliberate, security-motivated and recorded in MIGRATION_NOTES.md.
    //
    // MIGRATION: stage 1 is net-new - the legacy exception plumbing lived in an excluded HTTP module and error
    // page, so unhandled failures now leave through the framework's own IExceptionHandler as RFC 7807 problem
    // details, with no hand-rolled try/catch stage anywhere in the pipeline.
    app.UseApiPipeline();

    app.Run();
}
catch (Exception exception) when (exception is not HostAbortedException)
{
    // HostAbortedException is excluded because it is not a failure. It is how the host is unwound when a
    // tool builds this application without running it - the design-time migration tooling and the
    // integration-test host both do exactly that - and reporting those as fatal would put a false error in
    // every test run.

    // MIGRATION: DIVERGENCE, AND IT IS THE SAME ONE THE REQUEST PATH ALREADY MAKES. The exception OBJECT used
    // to be handed to the logger, which records its message, the message of every inner exception and the
    // stack trace. Startup is the worst place in the application for that: the failures that reach here are
    // configuration and connection failures, and their messages are exactly where a rejected connection
    // string, a key that was not found or a value that would not parse gets quoted verbatim - by framework
    // and provider code whose wording this solution does not author and cannot vouch for. The entry then
    // lands in the container's stdout, which is the most widely shipped and longest retained log a deployment
    // has.
    //
    // What is recorded instead is the bounded projection the request path already uses:
    // GlobalExceptionHandler.DescribeForDiagnostics walks the inner-exception chain to a fixed depth and
    // emits, for each link, the type's full name and its stack trace - method, type and, where symbols are
    // published, file and line. That is the part that identifies a defect and none of it is caller input. The
    // MESSAGES are the part that cannot be vouched for, and they are dropped.
    //
    // The policy is SHARED rather than restated. That member is internal precisely so every site with this
    // obligation implements it once - the request-logging middleware is the third - because a second
    // hand-rolled projection would drift from the first and the drift would be invisible until something
    // sensitive had already been written.
    Log.Fatal(
        "The API terminated unexpectedly during startup or shutdown. {Failure}",
        GlobalExceptionHandler.DescribeForDiagnostics(exception));

    // Rethrown, not swallowed. Logging a startup failure must not turn a process that failed to start into
    // one that reports success: the container orchestrator decides whether to restart this service from the
    // exit code, so the exception has to reach the runtime.
    throw;
}
finally
{
    // Buffered sink output would otherwise be lost on the way out, which is precisely when the last few
    // entries are the ones worth having.
    Log.CloseAndFlush();
}

/// <summary>Entry point for the API host.</summary>
/// <remarks>
/// Declared explicitly, publicly and in the global namespace because the integration test suite
/// builds this application through the framework's test host, which is generic over the entry-point
/// type and must be able to name it; the class the compiler generates for top-level statements is
/// internal, and this solution uses no assembly-level friend declaration. It is deliberately empty
/// - a member here would put application behaviour in a type that exists only to be named.
/// </remarks>
public partial class Program
{
}
