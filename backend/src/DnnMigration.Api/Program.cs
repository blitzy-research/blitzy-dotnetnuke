using DnnMigration.Api.Extensions;
using DnnMigration.Application;
using DnnMigration.Infrastructure;
using Serilog;
using Serilog.Formatting.Compact;

// The composition root, and nothing else. Every registration lives behind one of the
// extension methods below, and every pipeline stage lives behind UseApiPipeline, so
// this file stays short enough that the shape of the application is readable in one
// screen: configure logging, register, compose, run.

// A bootstrap logger, established before anything else can fail. Configuration has
// not been read at this point, so this logger is deliberately unconfigurable - which
// is the property that makes it useful. The startup guards this application relies on
// fail closed on a weak signing key, an unbounded token lifetime, an unusable portal
// path or a missing connection string, and a fail-closed startup that emits nothing is
// a container that exits with no explanation. This logger is replaced below, once
// configuration is available, by the real one.
Log.Logger = new LoggerConfiguration()
    .WriteTo.Console(new CompactJsonFormatter())
    .CreateBootstrapLogger();

try
{
    WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

    // Replaces the bootstrap logger. Sinks and levels are read from configuration so
    // that a deployment can change them without a rebuild; the enrichers are attached
    // here because they are structural rather than a matter of taste. Reading from the
    // container as well as from configuration is what allows a sink to resolve a
    // registered service.
    builder.Host.UseSerilog((context, services, logger) => logger
        .ReadFrom.Configuration(context.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext());

    // Order matters in exactly one place. The API layer's own registration runs first
    // because it registers the controller services, and the versioned API explorer
    // configured by the documentation registration must be applied on top of those -
    // not before them. The remaining calls are independent of one another.
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

    app.UseApiPipeline();

    app.Run();
}
catch (Exception exception) when (exception is not HostAbortedException)
{
    // HostAbortedException is excluded because it is not a failure. It is how the host
    // is unwound when a tool builds this application without running it - the
    // design-time migration tooling and the integration-test host both do exactly
    // that - and reporting those as fatal would put a false error in every test run.
    Log.Fatal(exception, "The API terminated unexpectedly during startup or shutdown.");

    // Rethrown, not swallowed. Logging a startup failure must not turn a process that
    // failed to start into one that reports success: the container orchestrator decides
    // whether to restart this service from the exit code, so the exception has to reach
    // the runtime.
    throw;
}
finally
{
    // Buffered sink output would otherwise be lost on the way out, which is precisely
    // when the last few entries are the ones worth having.
    Log.CloseAndFlush();
}

/// <summary>
/// Entry point for the API host.
/// </summary>
/// <remarks>
/// <para>
/// Declared explicitly, and declared public, for one reason: the integration test
/// suite builds this application through the framework's test host, which is generic
/// over the entry-point type and therefore has to be able to name it. The class the
/// compiler generates for a file of top-level statements is internal, so without this
/// declaration the test host could not reference it and every integration test would
/// fail to compile.
/// </para>
/// <para>
/// It is deliberately empty. Nothing belongs here that is not already above: adding a
/// member would put application behaviour in a type that exists only to be named.
/// </para>
/// </remarks>
public partial class Program
{
}
