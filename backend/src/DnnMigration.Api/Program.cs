using DnnMigration.Api.ErrorHandling;
using DnnMigration.Api.Extensions;
using DnnMigration.Api.Logging;
using DnnMigration.Application;
using DnnMigration.Infrastructure;
using Serilog;
using Serilog.Formatting.Compact;

// MIGRATION: the legacy managed-module chain and page lifecycle are replaced by the explicit ordered stages
// mapped below rather than translated, and several responsibilities that lifecycle carried are deliberately
// not ported because the subsystem behind each is excluded: client cache policy and security headers now
// belong to the reverse proxy, skin, stylesheet and favicon handling to the single-page application, and
// affiliate tracking and site logging to subsystems this migration does not carry.

// A bootstrap logger, established before anything else can fail and deliberately unconfigurable, because
// configuration has not been read yet.
Log.Logger = new LoggerConfiguration()
    .WriteTo.Console(new CompactJsonFormatter())
    .CreateBootstrapLogger();

try
{
    WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

    // Every deployment value this API reads is an ordinary configuration key, so it does not care which
    // provider supplies it - but a provider has to be REGISTERED before that statement is true of files.
    // docker/.env.example told operators that Docker Swarm, Kubernetes or a managed secret store could
    // deliver the connection string and the signing key as mounted files "without any code change", and no
    // key-per-file source was registered anywhere: a /run/secrets mount was simply not read.
    string secretsDirectory =
        builder.Configuration[ServiceCollectionExtensions.SecretsDirectorySectionName]
                is { Length: > 0 } configuredSecretsDirectory
            ? configuredSecretsDirectory
            : ServiceCollectionExtensions.DefaultSecretsDirectory;

    builder.Configuration.AddKeyPerFile(secretsDirectory, optional: true);

    builder.Host.UseSerilog((context, services, logger) => logger
        .ReadFrom.Configuration(context.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext()

        // Runs after the logging-scope properties are attached, the only position from which one of them
        // can be taken back off: the hosting layer scopes the raw request path over every entry written
        // while a request is handled, so removing the path from one message template would leave the same
        // value on the same entry under a different name.
        .Enrich.With(new SensitiveLogPropertyScrubber()));

    // Order matters in exactly one place: the API layer's own registration runs first because it registers
    // the controller services, and the versioned API explorer configured by the documentation registration
    // must be applied on top of those. The remaining calls are independent of one another.
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

    DnnMigration.Infrastructure.DependencyInjection.ValidateRefreshTokenStoreTopology(app.Services);

    app.UseApiPipeline();

    app.Run();
}
catch (Exception exception) when (exception is not HostAbortedException)
{
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
public partial class Program
{
}
