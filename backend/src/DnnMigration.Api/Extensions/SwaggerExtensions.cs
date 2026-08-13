using System.Globalization;
using System.Reflection;
using Asp.Versioning;
using Asp.Versioning.ApiExplorer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.AspNetCore.Mvc.Formatters;
using Microsoft.AspNetCore.Mvc.ModelBinding.Metadata;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace DnnMigration.Api.Extensions;

/// <summary>
/// The sole configuration point for API versioning and OpenAPI document generation in this application.
/// Nothing else in the solution configures either concern.
/// </summary>
/// <remarks>
/// <para>
/// Three concerns are registered together because none is useful without the other two: version selection
/// read from the URL PATH and from nowhere else, which is what turns a controller template into the
/// mandated <c>/api/v1/...</c> address space; a version-aware API explorer, without which the generated
/// document would be empty because the stock explorer cannot see versioned routes; and document generation,
/// one document per discovered version carrying the bearer definition the console needs in order to call a
/// protected operation.
/// </para>
/// <para>
/// <b>Exposure is environment-gated.</b> Registration always happens so the document can always be
/// produced, but the interactive console is mounted only when the caller says so - see <see
/// cref="UseSwaggerDocumentation(IApplicationBuilder, IWebHostEnvironment)"/>.
/// </para>
/// </remarks>
public static class SwaggerExtensions
{
    // MIGRATION: the legacy Web Forms application published no machine-readable API contract of any kind -
    // its surface described itself only to a browser - so every OpenAPI artefact produced by this file is
    // net-new behaviour rather than a port of something that already existed.

    /// <summary>
    /// Name of the bearer security definition. Declared once so the definition and the requirement that
    /// points at it cannot drift apart: a requirement naming no defined scheme is silently ignored, leaving
    /// the console unable to authorise anything.
    /// </summary>
    private const string BearerSecuritySchemeName = "Bearer";

    /// <summary>
    /// The media type RFC 7807 section 3 registers for a problem document, and the one this API labels
    /// every problem document with. Declared here so the runtime and the two filters that consult it cannot
    /// disagree by a typing error.
    /// </summary>
    private const string ProblemContentType = "application/problem+json";

    /// <summary>
    /// The scheme value written into the document. The OpenAPI specification requires the lower-case form
    /// here; the capitalised spelling is the definition's NAME, which is a different thing.
    /// </summary>
    private const string BearerSchemeValue = "bearer";

    private const string BearerTokenFormat = "JWT";

    private const string AuthorizationHeaderName = "Authorization";

    /// <summary>
    /// Format that turns an API version into a document group name. <c>VVV</c> emits the shortest
    /// unambiguous form, so version 1.0 becomes the group <c>v1</c>.
    /// </summary>
    private const string VersionGroupNameFormat = "'v'VVV";

    /// <summary>
    /// Group name of the default version, matching what <see cref="VersionGroupNameFormat"/> produces for
    /// it. Used only as the documented fallback described on <see
    /// cref="ConfigureSwaggerGenerationOptions"/>.
    /// </summary>
    private const string DefaultDocumentName = "v1";

    /// <summary>
    /// Path the interactive console is mounted under. Never the site root: the static front end owns
    /// <c>/</c> and its fallback route, so a console there would shadow the application itself.
    /// </summary>
    private const string DocumentationRoutePrefix = "swagger";

    /// <summary>
    /// Configuration key a deployment must set to <see langword="true"/> before the interactive console is
    /// published outside development: <c>Swagger:Enabled</c>. Public so a deployment's configuration, and
    /// any test asserting that the default is off, can name the key rather than repeat the string.
    /// </summary>
    public const string EnabledSectionName = "Swagger:Enabled";

    private const string DocumentTitle = "DnnMigration API";

    private const string DocumentDescription =
        "Administration API for the DnnMigration platform. Serves the Angular administration " +
        "client over JSON and reads the pre-existing SQL Server schema unchanged.";

    private const string DeprecationNotice =
        " This API version is deprecated. Migrate to the newest supported version.";

    private const string BearerSchemeDescription =
        "JSON Web Token issued by the sign-in endpoint. Paste the raw token value on its own: " +
        "the scheme name is prepended automatically, so including it here produces a malformed header.";

    private const int DefaultMajorVersion = 1;

    private const int DefaultMinorVersion = 0;

    /// <summary>
    /// Registers path-based API versioning, the version-aware API explorer and OpenAPI document generation.
    /// </summary>
    /// <remarks>
    /// The three registrations are chained because each depends on the one before it. Documents themselves
    /// are declared by <see cref="ConfigureSwaggerGenerationOptions"/> from the versions discovered at
    /// runtime, so adding a version needs no edit here; everything that does not vary by version is
    /// configured inline below.
    /// </remarks>
    /// <param name="services">The service collection to add the registrations to.</param>
    /// <returns>The same <paramref name="services"/> instance, so calls can be chained.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is <see langword="null"/>.</exception>
    public static IServiceCollection AddSwaggerDocumentation(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services
            .AddApiVersioning(options =>
            {
                options.DefaultApiVersion = new ApiVersion(DefaultMajorVersion, DefaultMinorVersion);

                // MIGRATION: DIVERGENCE. This was true, so a request naming no version was answered by the
                // default one.
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

                // Replace the version route parameter with its concrete value when describing an operation.
                // Omitting this leaves the unresolved parameter token in every documented path and breaks
                // the console's execute button against a route that cannot be matched.
                options.SubstituteApiVersionInUrl = true;
            });

        services.AddSwaggerGen(options =>
        {
            options.AddSecurityDefinition(BearerSecuritySchemeName, CreateBearerScheme());

            // MIGRATION: DIVERGENCE. AddSecurityRequirement was called here, which applies the bearer
            // requirement to the WHOLE DOCUMENT - including the login and refresh endpoints a caller has no
            // token yet to call, and /health, which the orchestrator probes with no credential at all.
            options.OperationFilter<AuthorizationOperationFilter>();

            options.OperationFilter<DeclaredResponseContentTypeOperationFilter>();

            // Labels every documented problem document with the media type it is actually served as. The
            // JSON formatter reports application/json first, so every refusal in the document claimed a
            // media type the API does not send.
            options.OperationFilter<ProblemResponseContentTypeOperationFilter>();

            // Declares the refusals the TRANSPORT can produce for an operation the explorer cannot see,
            // because no action code produces them: 405 is decided by routing and 413 and 415 by the host
            // and the formatter selector before any action runs.
            options.OperationFilter<TransportRefusalResponseOperationFilter>();

            options.OperationFilter<DerivedQueryParameterOperationFilter>();

            options.SchemaFilter<EnumMemberNameSchemaFilter>();

            // Reflects the solution-wide nullable annotations into the schema's nullability flags. A
            // schema-shape concern only: it changes no serialisation behaviour.
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
    /// Mounts the OpenAPI document endpoints and the interactive console while running in the development
    /// environment, and leaves the pipeline untouched everywhere else.
    /// </summary>
    /// <remarks>
    /// An interactive console published on a production port is an unauthenticated, machine-readable
    /// description of every endpoint, parameter and error shape this application accepts, so this overload
    /// has no way to publish it outside development: the decision is the environment name and nothing else.
    /// </remarks>
    /// <param name="app">The pipeline being composed.</param>
    /// <param name="environment">Environment used to decide whether the console is published.</param>
    /// <returns>The same <paramref name="app"/> instance, so calls can be chained.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="app"/> or <paramref name="environment"/> is <see langword="null"/>.
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
    /// Mounts the OpenAPI document endpoints and the interactive console, publishing them unconditionally
    /// in development and, outside development, only when a deployment has explicitly opted in - and then
    /// only to authenticated callers.
    /// </summary>
    /// <remarks>
    /// The default is off: absent configuration, an empty value, and a value that parses as <see
    /// langword="false"/> all leave the console unpublished.
    /// </remarks>
    /// <param name="app">The pipeline being composed.</param>
    /// <param name="environment">Environment used to decide whether the opt-in is even consulted.</param>
    /// <param name="configuration">
    /// Configuration read for <see cref="EnabledSectionName"/> outside development.
    /// </param>
    /// <returns>The same <paramref name="app"/> instance, so calls can be chained.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="app"/>, <paramref name="environment"/> or <paramref name="configuration"/> is <see
    /// langword="null"/>.
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
    /// Mounts the document endpoints and the interactive console into the supplied pipeline,
    /// unconditionally.
    /// </summary>
    /// <remarks>
    /// Private on purpose: every decision about WHETHER the console is published belongs to one of the two
    /// public overloads above, so the answer is always traceable to an environment name or a named
    /// configuration value.
    /// </remarks>
    /// <param name="app">The pipeline being composed.</param>
    /// <returns>The same <paramref name="app"/> instance, so calls can be chained.</returns>
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
                options.SwaggerEndpoint(
                    BuildDocumentPath(DefaultDocumentName),
                    DefaultDocumentName.ToUpperInvariant());
            }
        });

        return app;
    }

    private static string BuildDocumentPath(string groupName) =>
        $"/{DocumentationRoutePrefix}/{groupName}/swagger.json";

    /// <summary>
    /// Builds the bearer security definition. No credential, sample token or default value of any kind is
    /// embedded: the description explains what to paste and nothing more.
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
    /// Builds the requirement that points at the bearer definition, reusing <see
    /// cref="BearerSecuritySchemeName"/> so the two can never disagree.
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
    /// Feeds this assembly's generated XML descriptions into the document when that file is present beside
    /// the assembly.
    /// </summary>
    /// <remarks>
    /// The path is derived from the running assembly's own name and its base directory, so it is correct on
    /// a case-sensitive file system, inside a container and under an unprivileged account, and it never
    /// reaches outside the application directory.
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

    private static OpenApiInfo CreateDocumentInfo(string groupName, bool isDeprecated) => new()
    {
        Title = DocumentTitle,
        Version = groupName,
        Description = isDeprecated ? DocumentDescription + DeprecationNotice : DocumentDescription,
    };

    /// <summary>Declares one OpenAPI document per API version actually discovered at runtime.</summary>
    private sealed class ConfigureSwaggerGenerationOptions : IConfigureOptions<SwaggerGenOptions>
    {
        private readonly IApiVersionDescriptionProvider _versions;

        public ConfigureSwaggerGenerationOptions(IApiVersionDescriptionProvider versions)
        {
            ArgumentNullException.ThrowIfNull(versions);
            _versions = versions;
        }

        /// <summary>Declares a document for every discovered version.</summary>
        /// <param name="options">The generator options being configured.</param>
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

    /// <summary>Publishes the bearer requirement on the operations that actually enforce it.</summary>
    /// <remarks>
    /// <para>
    /// The requirement is decided per operation, from the same metadata the authorization middleware uses,
    /// so the document cannot come to disagree with the runtime: an endpoint that stops requiring a token
    /// stops advertising one in the same edit.
    /// </para>
    /// <para>
    /// The test is for the ABSENCE of <see cref="Microsoft.AspNetCore.Authorization.IAllowAnonymous"/>
    /// rather than the presence of an authorization attribute, which is the conservative direction: an
    /// operation protected by something this filter cannot see - a fallback policy, a convention, a
    /// requirement on a whole route branch - is still documented as needing a token.
    /// </para>
    /// </remarks>
    private sealed class AuthorizationOperationFilter : IOperationFilter
    {
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

    /// <summary>Publishes each response under the media type its own declaration names, where one is named.</summary>
    private sealed class DeclaredResponseContentTypeOperationFilter : IOperationFilter
    {
        public void Apply(OpenApiOperation operation, OperationFilterContext context)
        {
            ArgumentNullException.ThrowIfNull(operation);
            ArgumentNullException.ThrowIfNull(context);

            foreach (ProducesResponseTypeAttribute declaration in Declarations(context))
            {
                MediaTypeCollection declared = new();

                ((IApiResponseMetadataProvider)declaration).SetContentTypes(declared);

                if (declared.Count == 0)
                {
                    continue;
                }

                string status = declaration.StatusCode.ToString(CultureInfo.InvariantCulture);

                if (!operation.Responses.TryGetValue(status, out OpenApiResponse? response)
                    || response.Content.Count == 0)
                {
                    continue;
                }

                // The schema the generator already resolved is carried across unchanged. Only the key it
                // sits under changes, because the declared type of the payload is not in question here the
                // media type it is served as is.
                OpenApiMediaType body = response.Content.Values.First();

                response.Content.Clear();

                foreach (string mediaType in declared)
                {
                    response.Content[mediaType] = body;
                }
            }
        }

        /// <summary>Yields the per-response declarations attached to the described operation.</summary>
        /// <param name="context">The generator's context for the operation.</param>
        /// <returns>Every per-response declaration in scope for the operation.</returns>
        private static IEnumerable<ProducesResponseTypeAttribute> Declarations(OperationFilterContext context)
        {
            IList<object>? metadata = context.ApiDescription.ActionDescriptor.EndpointMetadata;

            if (metadata is not null)
            {
                return metadata.OfType<ProducesResponseTypeAttribute>();
            }

            return context.MethodInfo?
                       .GetCustomAttributes(inherit: true)
                       .OfType<ProducesResponseTypeAttribute>()
                   ?? [];
        }
    }

    /// <summary>
    /// Labels every documented problem-document response with the media type RFC 7807 registers for it.
    /// </summary>
    /// <remarks>
    /// The explorer keys a response body by the media types an output formatter can write for the declared
    /// type, and the JSON formatter reports <c>application/json</c> ahead of
    /// <c>application/problem+json</c>. Every refusal therefore claimed a media type the API does not send,
    /// so a generated client would be built to expect one and receive another.
    /// </remarks>
    private sealed class ProblemResponseContentTypeOperationFilter : IOperationFilter
    {
        private static readonly string[] ProblemSchemaIds =
            [nameof(ProblemDetails), nameof(ValidationProblemDetails)];

        public void Apply(OpenApiOperation operation, OperationFilterContext context)
        {
            ArgumentNullException.ThrowIfNull(operation);
            ArgumentNullException.ThrowIfNull(context);

            foreach (OpenApiResponse response in operation.Responses.Values)
            {
                if (response.Content.Count == 0)
                {
                    continue;
                }

                KeyValuePair<string, OpenApiMediaType> body = response.Content.First();

                if (!IsProblemDocument(body.Value))
                {
                    continue;
                }

                if (response.Content.Count == 1
                    && string.Equals(body.Key, ProblemContentType, StringComparison.Ordinal))
                {
                    continue;
                }

                response.Content.Clear();
                response.Content[ProblemContentType] = body.Value;
            }
        }

        private static bool IsProblemDocument(OpenApiMediaType body)
        {
            string? schemaId = body.Schema?.Reference?.Id;

            return schemaId is not null
                && ProblemSchemaIds.Contains(schemaId, StringComparer.Ordinal);
        }
    }

    /// <summary>
    /// Declares the refusals produced by the TRANSPORT rather than by an action, which the explorer cannot
    /// discover because no action code returns them.
    /// </summary>
    private sealed class TransportRefusalResponseOperationFilter : IOperationFilter
    {
        private static readonly (int Status, string Description)[] UniversalRefusals =
        [
            (StatusCodes.Status405MethodNotAllowed,
                "The route exists but does not accept this method. The response carries an Allow header "
                + "naming the methods it does accept, and an RFC 7807 body."),
        ];

        private static readonly (int Status, string Description)[] BodyRefusals =
        [
            (StatusCodes.Status413PayloadTooLarge,
                "The submitted body is larger than the configured request-size ceiling. The ceiling is a "
                + "deployment setting and is deliberately not quoted in the response."),
            (StatusCodes.Status415UnsupportedMediaType,
                "The body arrived under a media type this endpoint does not read. Submit "
                + "application/json."),
        ];

        public void Apply(OpenApiOperation operation, OperationFilterContext context)
        {
            ArgumentNullException.ThrowIfNull(operation);
            ArgumentNullException.ThrowIfNull(context);

            OpenApiSchema problemSchema = context.SchemaGenerator
                .GenerateSchema(typeof(ProblemDetails), context.SchemaRepository);

            foreach ((int status, string description) in UniversalRefusals)
            {
                Declare(operation, status, description, problemSchema);
            }

            if (operation.RequestBody is null)
            {
                return;
            }

            foreach ((int status, string description) in BodyRefusals)
            {
                Declare(operation, status, description, problemSchema);
            }
        }

        private static void Declare(
            OpenApiOperation operation,
            int status,
            string description,
            OpenApiSchema problemSchema)
        {
            string key = status.ToString(CultureInfo.InvariantCulture);

            if (operation.Responses.ContainsKey(key))
            {
                return;
            }

            operation.Responses[key] = new OpenApiResponse
            {
                Description = description,
                Content =
                {
                    [ProblemContentType] = new OpenApiMediaType { Schema = problemSchema },
                },
            };
        }
    }

    /// <summary>Removes query parameters that correspond to DERIVED model members, which cannot be bound.</summary>
    private sealed class DerivedQueryParameterOperationFilter : IOperationFilter
    {
        public void Apply(OpenApiOperation operation, OperationFilterContext context)
        {
            ArgumentNullException.ThrowIfNull(operation);
            ArgumentNullException.ThrowIfNull(context);

            if (operation.Parameters is null || operation.Parameters.Count == 0)
            {
                return;
            }

            HashSet<string> derived = new(StringComparer.Ordinal);

            foreach (ApiParameterDescription parameter in context.ApiDescription.ParameterDescriptions)
            {
                // Both conditions are load-bearing.
                if (parameter.Name is not null
                    && parameter.ModelMetadata is
                    {
                        MetadataKind: ModelMetadataKind.Property,
                        IsReadOnly: true,
                    })
                {
                    derived.Add(parameter.Name);
                }
            }

            if (derived.Count == 0)
            {
                return;
            }

            for (int index = operation.Parameters.Count - 1; index >= 0; index--)
            {
                OpenApiParameter published = operation.Parameters[index];

                if (published.In == ParameterLocation.Query && derived.Contains(published.Name))
                {
                    operation.Parameters.RemoveAt(index);
                }
            }
        }
    }

    /// <summary>Names the members of every integral enumeration the document publishes.</summary>
    private sealed class EnumMemberNameSchemaFilter : ISchemaFilter
    {
        public void Apply(OpenApiSchema schema, SchemaFilterContext context)
        {
            ArgumentNullException.ThrowIfNull(schema);
            ArgumentNullException.ThrowIfNull(context);

            if (!context.Type.IsEnum || schema.Enum is null || schema.Enum.Count == 0)
            {
                return;
            }

            string[] members = Enum.GetValues(context.Type)
                .Cast<object>()
                .Select(member => FormattableString.Invariant(
                    $"{Convert.ToInt64(member, CultureInfo.InvariantCulture)} = {member}"))
                .ToArray();

            if (members.Length == 0)
            {
                return;
            }

            string names = "Members: " + string.Join(", ", members) + ".";

            schema.Description = string.IsNullOrWhiteSpace(schema.Description)
                ? names
                : schema.Description.TrimEnd() + " " + names;
        }
    }
}
