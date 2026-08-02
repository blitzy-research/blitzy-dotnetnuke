using System.Reflection;
using Asp.Versioning;
using Asp.Versioning.ApiExplorer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace DnnMigration.Api.Extensions;

/// <summary>
/// The sole configuration point for API versioning and OpenAPI document generation
/// in this application. Nothing else in the solution configures either concern, so a
/// change to the exposed contract is made here and only here.
/// </summary>
/// <remarks>
/// <para>
/// Three concerns are registered together because none of them is useful without the
/// other two:
/// </para>
/// <list type="number">
///   <item>
///     <description>
///     <b>Versioning.</b> Version selection is read from the URL path, not from a
///     query string, not from a request header and not from a media type. That single
///     choice is what turns a controller template into the mandated
///     <c>/api/v1/...</c> address space.
///     </description>
///   </item>
///   <item>
///     <description>
///     <b>A version-aware API explorer.</b> The stock explorer cannot see versioned
///     routes at all; without the versioned replacement registered here the generated
///     document would be empty.
///     </description>
///   </item>
///   <item>
///     <description>
///     <b>Document generation.</b> One OpenAPI document per discovered version, each
///     carrying the bearer-token security definition that makes the interactive
///     console able to call protected operations.
///     </description>
///   </item>
/// </list>
/// <para>
/// <b>Contract every controller must honour.</b> Path-based version selection only
/// works when the version is actually present in the path, so each controller has to
/// spell the version parameter in its own route template - for example
/// <c>[Route("api/v{version:apiVersion}/portals")]</c>. A controller that omits the
/// parameter still compiles, still registers and still answers requests; it simply
/// answers them at an address with no version segment, silently falling outside the
/// documented address space. No compiler and no analyser reports this, which is why
/// the requirement is stated here rather than left to be inferred. Equally, no
/// controller may hard-code a literal version segment, because doing so freezes that
/// controller at one version and defeats the substitution described below.
/// </para>
/// <para>
/// <b>Parameter substitution.</b> The explorer is configured to replace the version
/// route parameter with its concrete value when it describes an operation. Without
/// that substitution every documented path would read <c>api/v{version}/...</c>
/// literally and the console's execute button would issue requests against a route
/// that does not exist.
/// </para>
/// <para>
/// <b>Exposure is environment-gated.</b> Registration always happens, so the document
/// can always be produced, but the interactive console is only mounted when the
/// caller says so - see the overloads of
/// <see cref="UseSwaggerDocumentation(IApplicationBuilder, IWebHostEnvironment)"/>.
/// An interactive console is a diagnostic surface and is not published on a
/// production port by default. When the console is switched off the returned pipeline
/// is byte-for-byte the one that was passed in, so a disabled console can never keep
/// the application from reporting itself healthy.
/// </para>
/// <para>
/// This type contributes no behaviour beyond registration and pipeline composition.
/// It holds no business rule, performs no data access, and deliberately leaves
/// controller registration, model-state handling and error shaping to the components
/// that own them.
/// </para>
/// </remarks>
public static class SwaggerExtensions
{
    // MIGRATION: the legacy Web Forms application published no machine-readable API
    // contract of any kind. Its surface was 15 .aspx pages, 147 .ascx controls, an
    // eight-entry HTTP module chain and a seven-entry handler chain declared in the
    // legacy site configuration, all of which described themselves only to a browser.
    // There is therefore no predecessor to translate here: every OpenAPI artefact
    // produced by this file is net-new behaviour, recorded as a deliberate addition
    // rather than as a port of something that already existed.

    /// <summary>
    /// Name of the bearer security definition. Declared once so that the definition
    /// and the requirement that points at it cannot drift apart: a requirement whose
    /// identifier does not match a defined scheme is silently ignored, leaving the
    /// interactive console unable to authorise anything.
    /// </summary>
    private const string BearerSecuritySchemeName = "Bearer";

    /// <summary>
    /// The scheme value written into the document. The OpenAPI specification requires
    /// the lower-case form here; the capitalised spelling is the definition's name,
    /// which is a different thing entirely.
    /// </summary>
    private const string BearerSchemeValue = "bearer";

    /// <summary>Token format advertised alongside the bearer scheme.</summary>
    private const string BearerTokenFormat = "JWT";

    /// <summary>Header the bearer token travels in.</summary>
    private const string AuthorizationHeaderName = "Authorization";

    /// <summary>
    /// Format that turns an API version into a document group name. <c>VVV</c> emits
    /// the shortest unambiguous form, so version 1.0 becomes the group <c>v1</c> and
    /// the document is served at <c>/swagger/v1/swagger.json</c>.
    /// </summary>
    private const string VersionGroupNameFormat = "'v'VVV";

    /// <summary>
    /// Group name of the default version, matching what
    /// <see cref="VersionGroupNameFormat"/> produces for the default version. Used
    /// only as the documented fallback described on
    /// <see cref="ConfigureSwaggerGenerationOptions"/>.
    /// </summary>
    private const string DefaultDocumentName = "v1";

    /// <summary>
    /// Path the interactive console is mounted under. Never the site root: the static
    /// front end owns <c>/</c> and its fallback route, so mounting a console there
    /// would shadow the application itself.
    /// </summary>
    private const string DocumentationRoutePrefix = "swagger";

    /// <summary>
    /// Configuration key a deployment must set to <see langword="true"/> before the
    /// interactive console is published outside development: <c>Swagger:Enabled</c>.
    /// </summary>
    /// <remarks>
    /// Public so that a deployment's configuration, and any test asserting the default
    /// is off, can name the key rather than repeat the string. The key's presence is
    /// never sufficient on its own - see
    /// <see cref="UseSwaggerDocumentation(IApplicationBuilder, IWebHostEnvironment, IConfiguration)"/>.
    /// </remarks>
    public const string EnabledSectionName = "Swagger:Enabled";

    /// <summary>Title shown on every generated document and in the console tab.</summary>
    private const string DocumentTitle = "DnnMigration API";

    /// <summary>Description shown on every generated document.</summary>
    private const string DocumentDescription =
        "Administration API for the DnnMigration platform. Serves the Angular administration " +
        "client over JSON and reads the pre-existing SQL Server schema unchanged.";

    /// <summary>Appended to the description of any version marked deprecated.</summary>
    private const string DeprecationNotice =
        " This API version is deprecated. Migrate to the newest supported version.";

    /// <summary>Guidance rendered next to the authorise control in the console.</summary>
    private const string BearerSchemeDescription =
        "JSON Web Token issued by the sign-in endpoint. Paste the raw token value on its own: " +
        "the scheme name is prepended automatically, so including it here produces a malformed header.";

    /// <summary>Major component of the version assumed when a request names none.</summary>
    private const int DefaultMajorVersion = 1;

    /// <summary>Minor component of the version assumed when a request names none.</summary>
    private const int DefaultMinorVersion = 0;

    /// <summary>
    /// Registers path-based API versioning, the version-aware API explorer and OpenAPI
    /// document generation.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The three registrations are chained because each depends on the one before it.
    /// The explorer registration also brings in the controller-side versioning
    /// support it needs, so no separate call is required for that.
    /// </para>
    /// <para>
    /// Documents themselves are declared by
    /// <see cref="ConfigureSwaggerGenerationOptions"/>, which is driven by the versions
    /// actually discovered at runtime rather than by a hard-coded list. Adding a
    /// version therefore needs no edit in this file. Everything that does not vary by
    /// version - the security definition, the XML description source and schema
    /// nullability - is configured inline below, where a reader looks for it.
    /// </para>
    /// </remarks>
    /// <param name="services">The service collection to add the registrations to.</param>
    /// <returns>
    /// The same <paramref name="services"/> instance, so calls can be chained.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="services"/> is <see langword="null"/>.
    /// </exception>
    public static IServiceCollection AddSwaggerDocumentation(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services
            .AddApiVersioning(options =>
            {
                options.DefaultApiVersion = new ApiVersion(DefaultMajorVersion, DefaultMinorVersion);

                // MIGRATION: DIVERGENCE. This was true, so a request that named no version
                // was answered by the default one. Combined with UrlSegmentApiVersionReader,
                // which is the only reader configured, that is incoherent: the version is a
                // path SEGMENT, so a request without it is a request to a different address,
                // not the same address with a field left blank. Accepting it means the
                // application answers both /api/v1/portals and /api/portals, and the second
                // form is silently pinned to whatever DefaultApiVersion happens to be. Every
                // such caller is then broken by the arrival of v2 - the retirement they were
                // never told about, because ReportApiVersions can only describe the version a
                // request actually named.
                //
                // False makes the segment mandatory: an address without it does not match, and
                // the caller is told so immediately instead of being bound to a default they
                // did not choose. DefaultApiVersion is retained because the explorer uses it to
                // name the document, not to fill in a missing segment.
                options.AssumeDefaultVersionWhenUnspecified = false;

                // Advertise the supported and deprecated versions on every response,
                // which lets a client discover a retirement without reading the docs.
                options.ReportApiVersions = true;

                // The version is read from the URL path and from nowhere else. This is
                // the single line that produces the mandated /api/v1/... address space.
                options.ApiVersionReader = new UrlSegmentApiVersionReader();
            })
            .AddApiExplorer(options =>
            {
                options.GroupNameFormat = VersionGroupNameFormat;

                // Replace the version route parameter with its concrete value when
                // describing an operation. Omitting this leaves the unresolved
                // parameter token in every documented path and breaks the console's
                // execute button against a route that cannot be matched.
                options.SubstituteApiVersionInUrl = true;
            });

        services.AddSwaggerGen(options =>
        {
            options.AddSecurityDefinition(BearerSecuritySchemeName, CreateBearerScheme());

            // MIGRATION: DIVERGENCE. AddSecurityRequirement was called here, which applies the
            // bearer requirement to the WHOLE DOCUMENT - every operation, including the ones
            // that must be reachable without a token. The login and refresh endpoints are the
            // ones a caller has no token yet to call, and /health is probed by the container
            // orchestrator with no credential at all, so documenting them as requiring a
            // bearer token described the opposite of the contract and made the console send an
            // Authorization header where none belongs. The requirement is now attached per
            // operation by AuthorizationOperationFilter, which reads the same [AllowAnonymous]
            // the runtime reads.
            options.OperationFilter<AuthorizationOperationFilter>();

            // Reflects the solution-wide nullable annotations into the schema's own
            // nullability flags. This is a schema-shape concern only; it changes no
            // serialisation behaviour, so the boundary contract for values that are
            // meaningful when empty or zero is unaffected.
            options.SupportNonNullableReferenceTypes();

            TryIncludeXmlDescriptions(options);
        });

        // Idempotent by construction: registering this configuration twice would
        // declare every document twice, so the enumerable registration is guarded.
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IConfigureOptions<SwaggerGenOptions>, ConfigureSwaggerGenerationOptions>());

        return services;
    }

    /// <summary>
    /// Mounts the OpenAPI document endpoints and the interactive console while running
    /// in the development environment, and leaves the pipeline untouched everywhere
    /// else.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An interactive console is a diagnostic surface, and one published on a
    /// production port is an unauthenticated, machine-readable description of every
    /// endpoint, parameter and error shape this application accepts. This overload
    /// therefore has no way to publish it outside development: the decision is the
    /// environment name and nothing else, so no caller can widen it.
    /// </para>
    /// <para>
    /// A deployment that genuinely needs the console outside development uses
    /// <see cref="UseSwaggerDocumentation(IApplicationBuilder, IWebHostEnvironment, IConfiguration)"/>,
    /// which requires an explicit configuration opt-in and restricts the console to
    /// authenticated callers. There is deliberately no overload that takes the decision
    /// as a plain argument - such a parameter records the answer without recording
    /// where it came from, which is what allowed production exposure to be one
    /// <see langword="true"/> away.
    /// </para>
    /// </remarks>
    /// <param name="app">The pipeline being composed.</param>
    /// <param name="environment">
    /// Environment used to decide whether the console is published.
    /// </param>
    /// <returns>
    /// The same <paramref name="app"/> instance, so calls can be chained.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="app"/> or <paramref name="environment"/> is
    /// <see langword="null"/>.
    /// </exception>
    public static IApplicationBuilder UseSwaggerDocumentation(
        this IApplicationBuilder app,
        IWebHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(environment);

        return environment.IsDevelopment() ? Mount(app) : app;
    }

    /// <summary>
    /// Mounts the OpenAPI document endpoints and the interactive console, publishing
    /// them unconditionally in development and, outside development, only when a
    /// deployment has explicitly opted in - and then only to authenticated callers.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The default is off. Absent configuration, a blank value, or any value other
    /// than a parsable <see langword="true"/> all leave the console unpublished, so
    /// exposure outside development is always the result of a deliberate, recorded
    /// decision in a deployment's own configuration.
    /// </para>
    /// <para>
    /// <b>Opting in does not make the console public.</b> Outside development the
    /// mounted stages run only for a caller who has already authenticated. An
    /// anonymous request is passed straight through untouched, and is then refused
    /// downstream on exactly the same terms as any other address that matches no
    /// endpoint - so the response distinguishes neither a console that is switched off
    /// from one that is switched on, nor either from a path that was never served at
    /// all. This gate is written explicitly because the document endpoints are
    /// middleware rather than routed endpoints: they carry no authorisation metadata,
    /// so neither an authorisation attribute nor a fallback policy can be attached to
    /// them.
    /// </para>
    /// <para>
    /// <b>Ordering requirements, and there are two.</b> This overload must be placed
    /// after the authentication stage, because outside development its gate reads the
    /// authenticated caller; placed earlier the gate would see every caller as
    /// anonymous and the console would never appear, which fails safe but looks like a
    /// broken opt-in. It must also be placed before the authorisation stage, because
    /// the mounted stages answer the request themselves and an authorisation stage
    /// carrying a fallback policy would otherwise refuse the console before it was
    /// reached - including in development, where the gate is absent.
    /// </para>
    /// <para>
    /// Authentication is the floor, not the whole control. A console reachable by any
    /// authenticated caller still describes the entire API to the least privileged
    /// account, so a deployment that opts in should also restrict the route at its
    /// reverse proxy or network boundary.
    /// </para>
    /// </remarks>
    /// <param name="app">The pipeline being composed.</param>
    /// <param name="environment">
    /// Environment used to decide whether the opt-in is even consulted.
    /// </param>
    /// <param name="configuration">
    /// Configuration read for <see cref="EnabledSectionName"/> outside development.
    /// </param>
    /// <returns>
    /// The same <paramref name="app"/> instance, so calls can be chained.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="app"/>, <paramref name="environment"/> or
    /// <paramref name="configuration"/> is <see langword="null"/>.
    /// </exception>
    public static IApplicationBuilder UseSwaggerDocumentation(
        this IApplicationBuilder app,
        IWebHostEnvironment environment,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(configuration);

        if (environment.IsDevelopment())
        {
            return Mount(app);
        }

        if (!configuration.GetValue<bool>(EnabledSectionName))
        {
            return app;
        }

        return app.UseWhen(
            static context => context.User.Identity?.IsAuthenticated == true,
            branch => Mount(branch));
    }

    /// <summary>
    /// Mounts the document endpoints and the interactive console into the supplied
    /// pipeline, unconditionally.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Private on purpose. Every decision about <em>whether</em> the console is
    /// published belongs to one of the two public overloads above, so that the answer
    /// is always traceable to an environment name or to a named configuration value.
    /// This method only knows <em>how</em>.
    /// </para>
    /// <para>
    /// The listed documents are taken from the versions discovered at runtime, so a
    /// new version appears in the console without an edit here. The document address
    /// is written relative to the site root, which is correct because this application
    /// is hosted at the root of its container port; no base path is applied.
    /// </para>
    /// </remarks>
    /// <param name="app">The pipeline being composed.</param>
    /// <returns>
    /// The same <paramref name="app"/> instance, so calls can be chained.
    /// </returns>
    private static IApplicationBuilder Mount(IApplicationBuilder app)
    {
        app.UseSwagger();

        app.UseSwaggerUI(options =>
        {
            // Deliberately not the site root. The static front end owns "/" and its
            // fallback route, so a console mounted there would shadow the application.
            options.RoutePrefix = DocumentationRoutePrefix;
            options.DocumentTitle = DocumentTitle;

            IApiVersionDescriptionProvider versions =
                app.ApplicationServices.GetRequiredService<IApiVersionDescriptionProvider>();

            bool anyVersionListed = false;

            foreach (ApiVersionDescription description in
                versions.ApiVersionDescriptions.OrderBy(version => version.GroupName, StringComparer.Ordinal))
            {
                options.SwaggerEndpoint(
                    BuildDocumentPath(description.GroupName),
                    description.GroupName.ToUpperInvariant());
                anyVersionListed = true;
            }

            if (!anyVersionListed)
            {
                // Matches the fallback document declared by
                // ConfigureSwaggerGenerationOptions, so the console and the generator
                // agree even in the degenerate case where no version was discovered.
                options.SwaggerEndpoint(
                    BuildDocumentPath(DefaultDocumentName),
                    DefaultDocumentName.ToUpperInvariant());
            }
        });

        return app;
    }

    /// <summary>
    /// Builds the root-relative address of a generated document for the given version
    /// group, matching the route the document middleware serves.
    /// </summary>
    /// <param name="groupName">Version group name, for example <c>v1</c>.</param>
    /// <returns>The document address, for example <c>/swagger/v1/swagger.json</c>.</returns>
    private static string BuildDocumentPath(string groupName) =>
        $"/{DocumentationRoutePrefix}/{groupName}/swagger.json";

    /// <summary>
    /// Builds the bearer security definition. No credential, sample token or default
    /// value of any kind is embedded: the description explains what to paste and
    /// nothing more.
    /// </summary>
    /// <returns>The bearer scheme to publish in the document.</returns>
    private static OpenApiSecurityScheme CreateBearerScheme() => new()
    {
        // Http rather than ApiKey, so the console renders a single token field and
        // prepends the scheme name itself instead of expecting a pre-formatted header.
        Type = SecuritySchemeType.Http,
        Scheme = BearerSchemeValue,
        BearerFormat = BearerTokenFormat,
        In = ParameterLocation.Header,
        Name = AuthorizationHeaderName,
        Description = BearerSchemeDescription,
    };

    /// <summary>
    /// Builds the requirement that points at the bearer definition, reusing
    /// <see cref="BearerSecuritySchemeName"/> so the two can never disagree.
    /// </summary>
    /// <returns>The security requirement to publish in the document.</returns>
    private static OpenApiSecurityRequirement CreateBearerRequirement()
    {
        OpenApiSecurityScheme reference = new()
        {
            Reference = new OpenApiReference
            {
                Type = ReferenceType.SecurityScheme,
                Id = BearerSecuritySchemeName,
            },
        };

        return new OpenApiSecurityRequirement
        {
            [reference] = Array.Empty<string>(),
        };
    }

    /// <summary>
    /// Feeds this assembly's generated XML descriptions into the document when that
    /// file is present beside the assembly.
    /// </summary>
    /// <remarks>
    /// The path is derived from the running assembly's own name and its base
    /// directory, so it is correct on a case-sensitive Linux file system, inside a
    /// container, and under an unprivileged account, and it never reaches outside the
    /// application directory. Existence is checked first because pointing the
    /// generator at a missing file throws while the application is starting: the
    /// process would then never report itself healthy, the orchestrator's health
    /// condition would never be satisfied and nothing waiting on it would start. A
    /// missing description file must therefore cost descriptions, never availability.
    /// </remarks>
    /// <param name="options">The generator options being configured.</param>
    private static void TryIncludeXmlDescriptions(SwaggerGenOptions options)
    {
        string? assemblyName = Assembly.GetExecutingAssembly().GetName().Name;

        if (string.IsNullOrEmpty(assemblyName))
        {
            return;
        }

        string descriptionFile = Path.Combine(AppContext.BaseDirectory, $"{assemblyName}.xml");

        if (File.Exists(descriptionFile))
        {
            options.IncludeXmlComments(descriptionFile, includeControllerXmlComments: true);
        }
    }

    /// <summary>
    /// Builds the document metadata for one discovered version.
    /// </summary>
    /// <param name="groupName">Version group name used as the document identifier.</param>
    /// <param name="isDeprecated">
    /// <see langword="true"/> when the version is marked deprecated, which appends a
    /// retirement notice to the description.
    /// </param>
    /// <returns>Metadata describing the document.</returns>
    private static OpenApiInfo CreateDocumentInfo(string groupName, bool isDeprecated) => new()
    {
        Title = DocumentTitle,
        Version = groupName,
        Description = isDeprecated ? DocumentDescription + DeprecationNotice : DocumentDescription,
    };

    /// <summary>
    /// Declares one OpenAPI document per API version actually discovered at runtime.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Kept as configuration rather than an inline call so the document set is derived
    /// from the versions the application really exposes. Introducing a second version
    /// therefore requires no change to this file at all.
    /// </para>
    /// <para>
    /// This type is private and nested on purpose: it is an implementation detail of
    /// <see cref="AddSwaggerDocumentation"/> and is not part of any public contract.
    /// </para>
    /// </remarks>
    private sealed class ConfigureSwaggerGenerationOptions : IConfigureOptions<SwaggerGenOptions>
    {
        private readonly IApiVersionDescriptionProvider _versions;

        /// <summary>
        /// Initialises the configuration with the discovered API versions.
        /// </summary>
        /// <param name="versions">Provider of the discovered API versions.</param>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="versions"/> is <see langword="null"/>.
        /// </exception>
        public ConfigureSwaggerGenerationOptions(IApiVersionDescriptionProvider versions)
        {
            ArgumentNullException.ThrowIfNull(versions);
            _versions = versions;
        }

        /// <summary>
        /// Declares a document for every discovered version.
        /// </summary>
        /// <remarks>
        /// If no version is reported - which would leave the generated specification,
        /// a required deliverable, unreachable - the default document is declared so
        /// the specification is always retrievable. The console's fallback listing
        /// uses the same name, so the two stay in agreement.
        /// </remarks>
        /// <param name="options">The generator options being configured.</param>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="options"/> is <see langword="null"/>.
        /// </exception>
        public void Configure(SwaggerGenOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);

            bool anyVersionDeclared = false;

            foreach (ApiVersionDescription description in _versions.ApiVersionDescriptions)
            {
                options.SwaggerDoc(
                    description.GroupName,
                    CreateDocumentInfo(description.GroupName, description.IsDeprecated));
                anyVersionDeclared = true;
            }

            if (!anyVersionDeclared)
            {
                options.SwaggerDoc(
                    DefaultDocumentName,
                    CreateDocumentInfo(DefaultDocumentName, isDeprecated: false));
            }
        }
    }

    /// <summary>
    /// Publishes the bearer requirement on the operations that actually enforce it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The document's security requirement is deliberately not declared globally. This filter
    /// decides per operation, from the same metadata the authorization middleware uses, so the
    /// document cannot come to disagree with the runtime: an endpoint that stops requiring a
    /// token stops advertising one in the same edit, with nothing here to keep in step.
    /// </para>
    /// <para>
    /// The test is for the ABSENCE of <see cref="Microsoft.AspNetCore.Authorization.IAllowAnonymous"/>
    /// rather than the presence of an authorization attribute. That is the conservative
    /// direction: an operation whose protection comes from a source this filter cannot see - a
    /// fallback policy, a convention, a requirement applied to a whole branch of the route
    /// table - is still documented as needing a token, whereas testing for the presence of an
    /// attribute would have quietly published such an operation as open. Being wrong towards
    /// "a token is needed" costs a reader one redundant header; being wrong the other way
    /// publishes a false contract.
    /// </para>
    /// <para>
    /// Endpoint metadata is read rather than the attributes of the method and its declaring
    /// type, because metadata is the merged view the framework itself resolves - it already
    /// accounts for an attribute inherited from the controller and for one contributed by a
    /// convention. Where no endpoint metadata is available the method and its declaring type
    /// are inspected directly, so a document generated outside a running endpoint graph is
    /// still described correctly.
    /// </para>
    /// </remarks>
    private sealed class AuthorizationOperationFilter : IOperationFilter
    {
        /// <summary>
        /// Adds the bearer requirement to <paramref name="operation"/> unless the operation
        /// allows anonymous access.
        /// </summary>
        /// <param name="operation">The operation being described.</param>
        /// <param name="context">The generator's context for the operation.</param>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="operation"/> or <paramref name="context"/> is
        /// <see langword="null"/>.
        /// </exception>
        public void Apply(OpenApiOperation operation, OperationFilterContext context)
        {
            ArgumentNullException.ThrowIfNull(operation);
            ArgumentNullException.ThrowIfNull(context);

            if (AllowsAnonymous(context))
            {
                return;
            }

            operation.Security.Add(CreateBearerRequirement());
        }

        /// <summary>
        /// Reports whether the described operation permits an unauthenticated caller.
        /// </summary>
        /// <param name="context">The generator's context for the operation.</param>
        /// <returns>
        /// <see langword="true"/> when anonymous access is allowed; otherwise
        /// <see langword="false"/>.
        /// </returns>
        private static bool AllowsAnonymous(OperationFilterContext context)
        {
            IList<object>? metadata = context.ApiDescription.ActionDescriptor.EndpointMetadata;

            if (metadata is not null)
            {
                return metadata.OfType<Microsoft.AspNetCore.Authorization.IAllowAnonymous>().Any();
            }

            MethodInfo? method = context.MethodInfo;

            if (method is null)
            {
                // Nothing to inspect, so the conservative answer applies and the operation is
                // documented as requiring a token.
                return false;
            }

            return method
                       .GetCustomAttributes(inherit: true)
                       .OfType<Microsoft.AspNetCore.Authorization.IAllowAnonymous>()
                       .Any()
                   || (method.DeclaringType?
                           .GetCustomAttributes(inherit: true)
                           .OfType<Microsoft.AspNetCore.Authorization.IAllowAnonymous>()
                           .Any()
                       ?? false);
        }
    }
}
