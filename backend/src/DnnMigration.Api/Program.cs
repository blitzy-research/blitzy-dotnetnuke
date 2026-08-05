using DnnMigration.Api.Extensions;
using DnnMigration.Api.Logging;
using DnnMigration.Application;
using DnnMigration.Infrastructure;
using Serilog;
using Serilog.Formatting.Compact;

// The composition root, and nothing else. Every registration lives behind one of the
// extension methods below, and every pipeline stage lives behind UseApiPipeline, so
// this file stays short enough that the shape of the application is readable in one
// screen: configure logging, register, compose, run.
//
// MIGRATION: this file is the whole of what replaced the legacy request pipeline, and
// the replacement is a deletion rather than a translation. Website/release.config
// declared eight managed modules at L67-L74 - ScriptModule, Compression, RequestFilter,
// UrlRewrite, Exception, UsersOnline, DNNMembership, Personalization - and seven
// handlers at L77-L83 for ScriptResource.axd, *_AppService.axd, *.asmx, Logoff.aspx,
// RSS.aspx, LinkClick.aspx and *.captcha.aspx, every one of them located through
// probing privatePath="bin;bin\HttpModules;bin\Providers;bin\Modules;bin\Support;".
// Website/Default.aspx.vb then ran a 700-line Web Forms page lifecycle on top of them.
// The module chain becomes the explicit, ordered stages mapped below; assembly probing
// has no counterpart in .NET 8 and needs none; and the page lifecycle has no
// counterpart at all, because an Angular application now renders the browser view.
//
// MIGRATION: six responsibilities that lifecycle carried are deliberately NOT ported,
// each because the subsystem behind it is excluded rather than because it was
// overlooked - client cache policy (Website/Default.aspx.vb:L119-L133, a switch over
// HostSettings("AuthenticatedCacheability") values "0" through "5", falling back to
// ServerAndNoCache), skin loading (:L217), stylesheet management (:L355), favicon
// handling (:L410), affiliate-cookie processing (:L306-L322, which also incremented
// affiliate statistics) and site logging (:L324-L351, whose storage mode came from
// HostSettings("SiteLogStorage") and defaulted to "D"). Cache policy and security
// headers now belong to the reverse proxy, the three presentation concerns belong to
// the single-page application, and the last two belong to subsystems this migration
// does not carry. MIGRATION_NOTES.md records each omission individually.

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
    //
    // The console sink, the configuration reader and the compact formatter all arrive
    // transitively with Serilog.AspNetCore 8.0.3, so reading configuration here adds no
    // package reference to this project.
    //
    // MIGRATION: the legacy audit trail survives the change of mechanism but not the
    // mechanism itself. Nineteen in-scope AddLog call sites wrote typed EventLogType
    // entries through a logging provider that is excluded; they are now structured
    // Serilog events emitted from the application services, which is why the minimum
    // level for this application's own namespaces must never be raised above
    // Information - doing so would silently discard the audit trail rather than the
    // noise. Nothing here captures a request body, and the EF Core database-command
    // log category stays at Warning in every appsettings overlay, because at
    // Information that category logs SQL parameter values.
    builder.Host.UseSerilog((context, services, logger) => logger
        .ReadFrom.Configuration(context.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext()

        // Runs after the logging-scope properties have been attached, which is the only
        // position from which one of them can be taken back off. The hosting layer opens a
        // scope carrying the raw request path over every entry written while a request is
        // handled - the audit events and the database entries included - so removing the
        // path from one message template would leave the same value on the same entry
        // under a different name. See SensitiveLogPropertyScrubber for what it removes and
        // why the path in particular cannot be kept.
        .Enrich.With(new SensitiveLogPropertyScrubber()));

    // Order matters in exactly one place. The API layer's own registration runs first
    // because it registers the controller services, and the versioned API explorer
    // configured by the documentation registration must be applied on top of those -
    // not before them. The remaining calls are independent of one another.
    //
    // MIGRATION: AddApiServices owns the JSON contract, and that contract is
    // constrained by the legacy null encoding rather than by taste. Null.vb:L36-L85
    // encodes absence in-band - NullInteger is -1, NullString is the empty string
    // rather than null, NullByte is 255, NullBoolean is False - while the schema seeds
    // Portals.PortalID at IDENTITY(-1,1) and Roles.RoleID, Tabs.TabID and
    // Modules.ModuleID at IDENTITY(0,1), and Globals.vb:L95-L98 separately reserves
    // "-1", "-2", "-3" and "-4" as role identifiers. Every one of -1, 0, "" and false
    // is therefore a legitimate value, so the serializer ignores nothing: it is
    // configured with DefaultIgnoreCondition = JsonIgnoreCondition.Never, and it must
    // never be switched to an ignore condition that omits nulls or default values -
    // neither in this file nor in that one, because either would erase a legitimate
    // identifier from every response. Property names are camelCase to match the SPA's
    // core/models/*.model.ts interfaces.
    //
    // MIGRATION: AddJwtAuthentication replaces the ASP.NET 2.0 membership and forms
    // authentication chain, and one legacy behaviour has no counterpart.
    // FormsAuthentication.SignOut cleared a server-recognised ticket; a bearer token is
    // self-contained and cannot be recalled, so POST /api/v1/auth/logout revokes the
    // refresh token and otherwise relies on the short access-token lifetime plus
    // client-side discard.
    //
    // MIGRATION: AddCredentialRateLimiting is the compensating control for a removed
    // guard. Login.ascx.vb:L162 gated authentication behind an optional CAPTCHA
    // (`If (UseCaptcha And ctlCaptcha.IsValid) OrElse (Not UseCaptcha)`); the control it
    // depended on is excluded with the rest of Library/Controls, so the brute-force
    // defence it provided is now a request budget on the credential endpoints. The
    // limiter is built into the shared framework, so this adds no package either.
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

    // The pipeline. Its order is fixed and non-negotiable, and it is mapped here rather
    // than only at its definition so that a reader of the composition root can see the
    // shape of a request without opening another file, and so that a reordering shows
    // up as a contradiction between two files instead of passing unnoticed in one:
    //
    //    1. exception handler        UseExceptionHandler()
    //    2. correlation id           CorrelationIdMiddleware
    //    3. request logging          RequestLoggingMiddleware
    //    4. routing                  UseRouting()
    //    5. CORS                     UseCors(named policy)
    //    6. authentication           UseAuthentication()
    //    7. authorization            UseAuthorization()
    //    8. portal-alias resolution  PortalAliasResolutionMiddleware
    //    9. controllers              MapControllers()
    //   10. health checks            MapHealthChecks("/health") liveness
    //                                MapHealthChecks("/health/ready") readiness
    //
    // Extensions/ApplicationBuilderExtensions.cs argues every position at length and is
    // the place to read for the reasoning; only one consequence belongs here, because it
    // reaches outside the pipeline. Stage 10 is ANONYMOUS AND MUST REMAIN SO: the image's
    // HEALTHCHECK probes it with wget before any credential exists in the system, and the
    // front-end service is held back by "condition: service_healthy" until it answers, so
    // requiring authorisation there would stop the deployment rather than secure it.
    //
    // Five further stages are interleaved there and none displaces a numbered one - HSTS
    // and HTTPS redirection, a tenant path-base stage, the credential rate limiter and
    // the documentation console. No URL or port is set anywhere: the container supplies
    // ASPNETCORE_URLS=http://+:8080 and runs unprivileged, so binding is its decision.
    //
    // MIGRATION: stage 1 is net-new behaviour rather than a port. The legacy exception
    // plumbing lived in an HTTP module and an error page, both excluded, and there
    // are zero in-scope exception-logging call sites - so there is no predecessor to
    // preserve. Unhandled failures now leave through the framework's own
    // IExceptionHandler as RFC 7807 problem details, uniformly, with no hand-rolled
    // try/catch stage anywhere in the pipeline.
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
/// It stays in the global namespace, because the test host names it unqualified, and it
/// is declared after the top-level statements, because the language requires that. An
/// assembly-level friend declaration would be the alternative to declaring it public,
/// and this solution deliberately uses none.
/// </para>
/// <para>
/// It is deliberately empty. Nothing belongs here that is not already above: adding a
/// member would put application behaviour in a type that exists only to be named.
/// </para>
/// </remarks>
public partial class Program
{
}
