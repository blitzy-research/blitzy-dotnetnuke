using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using DnnMigration.Api.Authorization;
using DnnMigration.Api.ErrorHandling;
using DnnMigration.Api.Filters;
using DnnMigration.Application.Abstractions;
using DnnMigration.Application.Dtos.Module;
using DnnMigration.Application.Options;
using DnnMigration.Application.Serialization;
using DnnMigration.Application.Validation;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.AspNetCore.DataProtection.XmlEncryption;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.HttpsPolicy;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Options;

using JsonOptions = Microsoft.AspNetCore.Http.Json.JsonOptions;

namespace DnnMigration.Api.Extensions;

/// <summary>
/// Registers the services the API layer itself contributes, and binds and validates every configuration
/// section other than the JWT settings.
/// </summary>
/// <remarks>
/// <para>
/// This file and its pipeline counterpart are what keep <c>Program.cs</c> a composition root rather than a
/// registration dump.
/// </para>
/// <para>
/// <b>Every bound section is validated, and validation is scheduled for startup.</b> A settings type
/// declares the bounds its values must satisfy; the validators nested below enforce them.
/// </para>
/// </remarks>
public static class ServiceCollectionExtensions
{
    /// <summary>Largest request body the API accepts: one mebibyte.</summary>
    /// <remarks>
    /// The server's own default is thirty megabytes, which for a JSON administration API is thirty
    /// megabytes of attacker-chosen allocation on every unauthenticated endpoint. This API's largest
    /// legitimate request is a portal or user record of a few kilobytes, so a mebibyte is generous by three
    /// orders of magnitude and still bounds the work a caller can create before any validator has run.
    /// </remarks>
    public const long MaximumRequestBodyBytes = 1L * 1024L * 1024L;

    /// <summary>Worst-case bytes one document character occupies once JSON-encoded: six.</summary>
    private const long MaximumJsonBytesPerCharacter = 6L;

    /// <summary>
    /// Headroom, in bytes, for everything in an import body that is not the document: sixty-four kibibytes.
    /// </summary>
    private const long ImportEnvelopeAllowanceBytes = 64L * 1024L;

    /// <summary>
    /// Largest request body the module import action accepts, computed from the document ceiling the import
    /// contract publishes.
    /// </summary>
    /// <remarks>
    /// The global limit above is deliberately left alone. The reverse proxy in front of the API bounds the
    /// body first and must therefore be at least this large; <c>docker/nginx.conf</c> sets its API
    /// location's <c>client_max_body_size</c> to exactly this value, because nginx's own default is one
    /// mebibyte and would otherwise be the binding limit of the whole path.
    /// </remarks>
    public const long MaximumImportRequestBodyBytes =
        (ModuleImportRequest.ContentCharacterMaximum * MaximumJsonBytesPerCharacter)
        + ImportEnvelopeAllowanceBytes;

    /// <summary>
    /// Configuration key listing the addresses of reverse proxies whose forwarded headers may be trusted:
    /// <c>Proxy:KnownProxies</c>.
    /// </summary>
    public const string KnownProxiesSectionName = "Proxy:KnownProxies";

    /// <summary>
    /// Configuration key listing the networks, in prefix notation, whose forwarded headers may be trusted:
    /// <c>Proxy:KnownNetworks</c>.
    /// </summary>
    public const string KnownNetworksSectionName = "Proxy:KnownNetworks";

    /// <summary>
    /// Configuration key naming the directory from which file-delivered secrets are read:
    /// <c>Secrets:Directory</c>.
    /// </summary>
    /// <remarks>
    /// Read by the composition root before the key-per-file source is added, so it can only be supplied by
    /// a source that has already been read - an <c>appsettings</c> entry, or the environment variable
    /// <c>Secrets__Directory</c>. That ordering is deliberate rather than a limitation: a directory named
    /// by the very files it would load could not be resolved at all.
    /// </remarks>
    public const string SecretsDirectorySectionName = "Secrets:Directory";

    /// <summary>
    /// The directory file-delivered secrets are read from when the deployment names none:
    /// <c>/run/secrets</c>.
    /// </summary>
    /// <remarks>
    /// The path <c>docker compose</c> and Docker Swarm mount a declared secret at, which makes the smallest
    /// step away from environment-variable delivery a compose <c>secrets:</c> block and no code change. The
    /// source is registered as optional, so on a host that mounts nothing - the base compose topology, the
    /// test host, a workstation - this path simply contributes no keys.
    /// </remarks>
    public const string DefaultSecretsDirectory = "/run/secrets";

    /// <summary>Configuration key listing the host names this API answers on: <c>AllowedHosts</c>.</summary>
    public const string PermittedHostsSectionName = "AllowedHosts";

    /// <summary>The separator the host-filter setting uses between entries.</summary>
    private const char PermittedHostsSeparator = ';';

    /// <summary>
    /// Reports whether the deployment has named at least one reverse proxy whose forwarded headers may be
    /// trusted.
    /// </summary>
    /// <param name="configuration">The application's configuration.</param>
    /// <returns>
    /// <see langword="true"/> when <see cref="KnownProxiesSectionName"/> or <see
    /// cref="KnownNetworksSectionName"/> names anything.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="configuration"/> is <see langword="null"/>.
    /// </exception>
    /// <remarks>
    /// The pipeline asks this before registering the forwarded-headers stage, so that the stage exists only
    /// where a deployment has declared what it trusts. That is a security decision rather than an
    /// optimisation: promoting a forwarded address supplied by an untrusted hop lets a caller name its own
    /// address, and therefore choose its own rate-limit partition.
    /// </remarks>
    public static bool HasTrustedProxies(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        return TrustedEntries(configuration, KnownProxiesSectionName).Count != 0
            || TrustedEntries(configuration, KnownNetworksSectionName).Count != 0;
    }

    /// <summary>Reads one trusted-hop section into the normalised entries every consumer must agree on.</summary>
    /// <param name="configuration">The application's configuration.</param>
    /// <param name="sectionName">
    /// <see cref="KnownProxiesSectionName"/> or <see cref="KnownNetworksSectionName"/>.
    /// </param>
    /// <returns>The declared entries, trimmed, with every blank or whitespace-only entry removed.</returns>
    /// <remarks>
    /// A blank entry is DROPPED rather than refused, and the direction matters: a deployment template that
    /// ships an empty entry is a template that has not configured a trusted hop, which is the safe reading.
    /// A NON-blank entry that is not an address or a network is still refused loudly by the parsers below,
    /// because that is a value an operator meant and got wrong.
    /// </remarks>
    private static IReadOnlyList<string> TrustedEntries(
        IConfiguration configuration,
        string sectionName) =>
        [.. (configuration.GetSection(sectionName).Get<string[]>() ?? [])
            .Where(entry => !string.IsNullOrWhiteSpace(entry))
            .Select(entry => entry.Trim())];

    /// <summary>Adds the API layer's own services and binds every configuration section it owns.</summary>
    /// <param name="services">The container being populated.</param>
    /// <param name="configuration">The application's configuration.</param>
    /// <returns>The same <paramref name="services"/> instance, so calls can be chained.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="services"/> or <paramref name="configuration"/> is <see langword="null"/>.
    /// </exception>
    /// <remarks>
    /// The order of the calls below is not arbitrary. The problem-details factory is registered before the
    /// controller services, because those services register the framework's own factory only if none is
    /// present; registering afterwards would leave the framework's factory in place and this application's
    /// error payload unused, with nothing to indicate it.
    /// </remarks>
    public static IServiceCollection AddApiServices(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        AddValidatedOptions(services, configuration);
        AddRequestContextAccessors(services);
        AddErrorHandling(services);
        AddControllerServices(services);
        AddRequestLimits(services);
        AddTransportSecurity(services, configuration);
        AddForwardedHeaders(services, configuration);
        AddEphemeralDataProtection(services);
        AddResponseCompressionPolicy(services);
        AddStartupDiagnostics(services);

        return services;
    }

    /// <summary>
    /// Registers response compression for the JSON this API returns, on the plain-HTTP path only.
    /// </summary>
    /// <param name="services">The container being populated.</param>
    /// <remarks>
    /// <para>
    /// A collection response is highly compressible - a performance review measured this payload class at
    /// 16.1 times - and the documented deployment already compresses on the way through the reverse proxy. The
    /// path that had NO compression at all is the direct one: a caller reaching Kestrel without the proxy in
    /// front of it, which is how the API is reached inside the container network and how it is reached in any
    /// deployment that terminates elsewhere. This registration covers that path, so the worst case is bounded
    /// wherever the caller entered.
    /// </para>
    /// <para>
    /// <b>Over HTTPS it stays off, and that is a security decision rather than an omission.</b> The framework
    /// disables compression on TLS connections by default because compressing a response that mixes a secret
    /// with attacker-influenced content is the CRIME and BREACH exposure; this API returns authenticated JSON,
    /// so the default is kept. Transport security terminates at the proxy in the documented topology and the
    /// proxy compresses there, which is where compressing an encrypted response belongs.
    /// </para>
    /// <para>
    /// Brotli is registered ahead of Gzip so a caller advertising both is served the smaller encoding, and the
    /// problem-details media type is added explicitly: the default MIME list covers <c>application/json</c>
    /// but not <c>application/problem+json</c>, so without it every error body - the responses a client is
    /// most likely to be reading in bulk while diagnosing - would travel uncompressed.
    /// </para>
    /// </remarks>
    private static void AddResponseCompressionPolicy(IServiceCollection services)
    {
        services.AddResponseCompression(options =>
        {
            options.EnableForHttps = false;

            options.Providers.Add<BrotliCompressionProvider>();
            options.Providers.Add<GzipCompressionProvider>();

            options.MimeTypes = ResponseCompressionDefaults.MimeTypes.Concat(
            [
                "application/problem+json",
            ]);
        });
    }

    /// <summary>
    /// Registers the one-shot start-up scan that reports stored portal aliases this deployment cannot
    /// address.
    /// </summary>
    /// <param name="services">The container being populated.</param>
    /// <remarks>
    /// The reason a hosted service exists at all - in a solution whose refresh-token topology validation is
    /// deliberately a post-build call precisely to avoid one - is argued on <see
    /// cref="Diagnostics.PortalAliasConformanceMonitor"/> and recorded in <c>MIGRATION_NOTES.md</c>.
    /// </remarks>
    private static void AddStartupDiagnostics(IServiceCollection services) =>
        services.AddHostedService<Diagnostics.PortalAliasConformanceMonitor>();

    /// <summary>
    /// Declares the data-protection key ring EPHEMERAL, because this application protects nothing that must
    /// outlive its own process.
    /// </summary>
    /// <param name="services">The container being populated.</param>
    /// <remarks>
    /// THIS REPLACES TWO STARTUP WARNINGS WITH A DECISION. The framework registers the data-protection
    /// stack whether or not an application uses it, and its default key ring is written to a directory
    /// under the account's home folder.
    /// </remarks>
    private static void AddEphemeralDataProtection(IServiceCollection services)
    {
        services.AddDataProtection()
            .AddKeyManagementOptions(options =>
            {
                options.XmlRepository = new InMemoryKeyRingRepository();
                options.XmlEncryptor = new NullXmlEncryptor();
            });
    }

    /// <summary>
    /// Binds the password-policy, portal and caching sections and schedules their validation for startup.
    /// </summary>
    /// <param name="services">The container being populated.</param>
    /// <param name="configuration">The application's configuration.</param>
    /// <remarks>
    /// The password-policy section is bound here rather than alongside the JWT settings even though both
    /// are security configuration, because three request validators resolve it and none of them is
    /// reachable from the authentication scheme.
    /// </remarks>
    private static void AddValidatedOptions(IServiceCollection services, IConfiguration configuration)
    {
        services
            .AddOptions<PasswordPolicyOptions>()
            .Bind(configuration.GetSection(PasswordPolicyOptions.SectionName))
            .ValidateOnStart();

        services.AddSingleton<IValidateOptions<PasswordPolicyOptions>, PasswordPolicyOptionsValidator>();

        // The legacy-credential migration window is deliberately not bound here: its section carries a
        // deployment-supplied decryption key, so the type holding it stays internal to Infrastructure,
        // beside the verifier that consumes it, rather than becoming a publicly reachable options type this
        // layer could hand to anything.

        services.AddSingleton(
            provider => provider.GetRequiredService<IOptions<PasswordPolicyOptions>>().Value);

        services
            .AddOptions<PortalOptions>()
            .Bind(configuration.GetSection(PortalOptions.SectionName))
            .ValidateOnStart();

        services.AddSingleton<IValidateOptions<PortalOptions>, PortalOptionsValidator>();

        services.AddSingleton(
            provider => provider.GetRequiredService<IOptions<PortalOptions>>().Value);

        services
            .AddOptions<CachingOptions>()
            .Bind(configuration.GetSection(CachingOptions.SectionName))
            .ValidateOnStart();

        services.AddSingleton<IValidateOptions<CachingOptions>, CachingOptionsValidator>();

        // Four application services - portal, module, user and tab - take a bound CachingOptions directly,
        // so the same projection is required for them.
        services.AddSingleton(
            provider => provider.GetRequiredService<IOptions<CachingOptions>>().Value);

        // The refresh-token store's shape and identity.
        services
            .AddOptions<RefreshTokenStoreOptions>()
            .Bind(configuration.GetSection(RefreshTokenStoreOptions.SectionName))
            .ValidateOnStart();

        services.AddSingleton<
            IValidateOptions<RefreshTokenStoreOptions>,
            RefreshTokenStoreOptionsValidator>();
    }

    /// <summary>
    /// Registers the accessor for the current request and the projection of the authenticated caller's
    /// claims.
    /// </summary>
    /// <param name="services">The container being populated.</param>
    /// <remarks>
    /// The claims projection is scoped rather than singleton because it reads the principal of the request
    /// being handled; capturing one in a singleton would pin the first caller's identity for the lifetime
    /// of the process.
    /// </remarks>
    private static void AddRequestContextAccessors(IServiceCollection services)
    {
        services.AddHttpContextAccessor();

        services.AddScoped<ICurrentUser, CurrentUser>();
    }

    /// <summary>
    /// Registers the problem-details service, this application's problem-details factory, and the handler
    /// that turns an escaped exception into a problem document.
    /// </summary>
    /// <param name="services">The container being populated.</param>
    /// <remarks>
    /// The problem-details service is not optional decoration. Two pipeline stages - the tenant resolver
    /// and the rate limiter's refusal path - resolve it directly in order to write their own refusals, and
    /// neither can be activated without it.
    /// </remarks>
    private static void AddErrorHandling(IServiceCollection services)
    {
        services.AddProblemDetails();

        services.AddSingleton<ProblemDetailsFactory, ValidationProblemDetailsFactory>();

        // The writer behind IProblemDetailsService serialises through the minimal-API options object, not
        // the controller formatters, so the enumeration policy has to be applied to both.
        services.Configure<JsonOptions>(options =>
        {
            DnnJsonConverters.AddTo(options.SerializerOptions);

            options.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.Never;
            options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        });

        services.AddExceptionHandler<GlobalExceptionHandler>();
    }

    /// <summary>Registers the controller services and the validation filter.</summary>
    /// <param name="services">The container being populated.</param>
    /// <remarks>
    /// The filter is added by type rather than as an instance, so it is activated per request from the
    /// container. It depends on the problem-details factory, and registering an instance would require
    /// constructing it here, before that factory exists.
    /// </remarks>
    private static void AddControllerServices(IServiceCollection services)
    {
        services
            .AddControllers(options =>
            {
                options.Filters.Add<FluentValidationActionFilter>();

                // The order is load-bearing: every controller carries [Produces("application/json")],
                // itself a result filter that clears and reassigns the result's content types, and at equal
                // order a controller-scoped filter runs AFTER a global one - so at the default order this
                // filter's work is overwritten on every response and the media type never changes.
                options.Filters.Add<ProblemDetailsContentTypeFilter>(order: 1);
            })
            .AddJsonOptions(options =>
            {
                // The application's own enumeration converters are added first, and the order is
                // load-bearing: a converter is selected by taking the first entry in this collection that
                // reports it can handle the type, so a general enumeration converter ahead of a specific
                // one would claim every enumeration and the specific one would never run.
                DnnJsonConverters.AddTo(options.JsonSerializerOptions);

                // No blanket enumeration converter is registered, and that is a decision: the remaining
                // enumerations on the wire - portal registration mode, banner advertising mode and module
                // visibility - travel as the integer discriminators their columns store, which is how the
                // Angular models consume them.

                // Every declared member is written, including one holding null: absence is expressed by the
                // member's VALUE being null, never by the member going missing.
                options.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.Never;

                options.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;

                options.JsonSerializerOptions.UnmappedMemberHandling =
                    JsonUnmappedMemberHandling.Disallow;
            });

        // The framework maps sixteen status codes to a problem-type link and a title, and 429 is not one of
        // them - it postdates the specification the defaults cite.
        services.Configure<ApiBehaviorOptions>(options =>
            options.ClientErrorMapping[StatusCodes.Status429TooManyRequests] = new ClientErrorData
            {
                Title = "Too Many Requests",
                Link = "https://tools.ietf.org/html/rfc6585#section-4",
            });
    }

    /// <summary>Bounds the size of a request body.</summary>
    /// <param name="services">The container being populated.</param>
    /// <remarks>
    /// Applied to the web server's own limits rather than through a filter, so it is enforced while the
    /// body is still being read off the socket. A limit applied after model binding would be applied after
    /// the allocation it exists to prevent.
    /// </remarks>
    private static void AddRequestLimits(IServiceCollection services)
    {
        services.Configure<KestrelServerOptions>(options =>
            options.Limits.MaxRequestBodySize = MaximumRequestBodyBytes);
    }

    /// <summary>Configures HTTPS redirection and strict transport security.</summary>
    /// <param name="services">The container being populated.</param>
    /// <param name="configuration">Configuration naming whether and where HTTPS is exposed.</param>
    /// <exception cref="InvalidOperationException">
    /// HTTPS redirection is enabled without a valid external TLS port.
    /// </exception>
    /// <remarks>
    /// <b>ONE registration, ONE key.</b> This concern was configured twice for a while, from two key names:
    /// this stage read <c>Https:RedirectPort</c> and validated it at start-up, while a second registration
    /// read a <c>Https:Port</c> of its own and defaulted it.
    /// </remarks>
    private static void AddTransportSecurity(
        IServiceCollection services,
        IConfiguration configuration)
    {
        bool redirectEnabled =
            configuration.GetValue<bool>(ApplicationBuilderExtensions.HttpsRedirectionSectionName);
        int? redirectPort =
            configuration.GetValue<int?>(ApplicationBuilderExtensions.HttpsRedirectPortSectionName);

        if (redirectEnabled && redirectPort is not (>= 1 and <= 65535))
        {
            throw new InvalidOperationException(
                $"'{ApplicationBuilderExtensions.HttpsRedirectPortSectionName}' must be a TCP port from 1 "
                + $"through 65535 when '{ApplicationBuilderExtensions.HttpsRedirectionSectionName}' is true.");
        }

        ValidatePermittedHosts(configuration);

        services.AddHttpsRedirection(options =>
        {
            options.RedirectStatusCode = StatusCodes.Status308PermanentRedirect;
            options.HttpsPort = redirectPort;
        });

        services.AddHsts(options =>
        {
            options.IncludeSubDomains = true;
            options.Preload = false;
            options.MaxAge = TimeSpan.FromDays(365);
        });
    }

    /// <summary>
    /// Refuses to start when the host filter names a documentation host rather than the deployment's own.
    /// </summary>
    /// <param name="configuration">Configuration carrying the host-filter setting.</param>
    /// <exception cref="InvalidOperationException">
    /// An entry is a name reserved for documentation, or an unreplaced editing marker.
    /// </exception>
    /// <remarks>
    /// <b>WHY THE HOST FILTER IS VALIDATED HERE AT ALL.</b> <c>AllowedHosts</c> is consumed by the
    /// framework's own host-filtering stage, which the generic host registers from this exact key, so
    /// nothing in this solution reads it and nothing would ever have noticed a wrong value.
    /// </remarks>
    private static void ValidatePermittedHosts(IConfiguration configuration)
    {
        string? configured = configuration[PermittedHostsSectionName];

        if (string.IsNullOrWhiteSpace(configured))
        {
            return;
        }

        foreach (string entry in configured.Split(
            PermittedHostsSeparator,
            StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (entry.Equals("*", StringComparison.Ordinal))
            {
                continue;
            }

            string? rejection = ReservedDeploymentHosts.DescribeRejection(entry);

            if (rejection is not null)
            {
                throw new InvalidOperationException(
                    $"'{entry}' cannot be an entry of '{PermittedHostsSectionName}': {rejection} Set the "
                    + "deployment's own public host name - in the container topology that is the single "
                    + "DNN_PUBLIC_HOST value, from which the host filter, the reverse proxy's server_name "
                    + "and the permitted browser origin are all derived.");
            }
        }
    }

    /// <summary>
    /// Configures which reverse proxies, if any, are trusted to report the original client's address and
    /// scheme.
    /// </summary>
    /// <param name="services">The container being populated.</param>
    /// <param name="configuration">The application's configuration.</param>
    /// <exception cref="InvalidOperationException">
    /// A configured proxy address or network cannot be parsed.
    /// </exception>
    private static void AddForwardedHeaders(IServiceCollection services, IConfiguration configuration)
    {
        IReadOnlyList<string> knownProxies = TrustedEntries(configuration, KnownProxiesSectionName);
        IReadOnlyList<string> knownNetworks = TrustedEntries(configuration, KnownNetworksSectionName);

        IPAddress[] proxies = [.. knownProxies.Select(ParseProxyAddress)];
        (IPAddress Prefix, int PrefixLength)[] networks = [.. knownNetworks.Select(ParseProxyNetwork)];

        services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            options.ForwardLimit = 1;

            if (proxies.Length == 0 && networks.Length == 0)
            {
                return;
            }

            options.KnownProxies.Clear();
            options.KnownNetworks.Clear();

            foreach (IPAddress proxy in proxies)
            {
                options.KnownProxies.Add(proxy);
            }

            foreach ((IPAddress prefix, int prefixLength) in networks)
            {
                // Target-typed deliberately. Two distinct types spell "IP network" on this framework - the
                // runtime's and the middleware's - and the one this collection holds is the middleware's
                // business, not this file's.
                options.KnownNetworks.Add(new(prefix, prefixLength));
            }
        });
    }

    /// <summary>Parses one trusted proxy address.</summary>
    /// <param name="value">The configured address.</param>
    /// <returns>The parsed address.</returns>
    /// <exception cref="InvalidOperationException">The value is not an address.</exception>
    private static IPAddress ParseProxyAddress(string value)
    {
        if (IPAddress.TryParse(value, out IPAddress? address))
        {
            return address;
        }

        throw new InvalidOperationException(
            $"The value '{value}' in '{KnownProxiesSectionName}' is not an IP address.");
    }

    /// <summary>Parses one trusted network in prefix notation.</summary>
    /// <param name="value">The configured network, for example <c>10.0.0.0/8</c>.</param>
    /// <returns>
    /// The network's base address and prefix length, both already checked for consistency with each other.
    /// </returns>
    /// <exception cref="InvalidOperationException">
    /// The value is not a network in prefix notation, or its prefix length is not valid for the address
    /// family.
    /// </exception>
    /// <remarks>
    /// The validated parts are returned rather than a constructed network, so that the caller constructs
    /// whichever network type the middleware's collection holds.
    /// </remarks>
    private static (IPAddress Prefix, int PrefixLength) ParseProxyNetwork(string value)
    {
        string[] parts = value.Split('/', 2);

        if (parts.Length != 2
            || !IPAddress.TryParse(parts[0], out IPAddress? prefix)
            || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int prefixLength))
        {
            throw new InvalidOperationException(
                $"The value '{value}' in '{KnownNetworksSectionName}' is not a network in prefix notation, such as '10.0.0.0/8'.");
        }

        int maximumPrefixLength = prefix.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? 128 : 32;

        if (prefixLength < 0 || prefixLength > maximumPrefixLength)
        {
            throw new InvalidOperationException(
                $"The prefix length {prefixLength} in '{value}' from '{KnownNetworksSectionName}' is not valid for the address family; it must be between 0 and {maximumPrefixLength}.");
        }

        return (prefix, prefixLength);
    }

    /// <summary>
    /// Checks the bound password policy against the bounds <see cref="PasswordPolicyOptions"/> declares.
    /// </summary>
    /// <remarks>
    /// Every rule here is either a <em>floor</em> that stops the legacy policy being weakened, or a check
    /// that a setting is <em>satisfiable</em>. None of them tightens the policy: the migration preserves it
    /// verbatim precisely so that users the legacy installation accepted are not locked out at the moment
    /// they are migrated.
    /// </remarks>
    private sealed class PasswordPolicyOptionsValidator : IValidateOptions<PasswordPolicyOptions>
    {
        /// <summary>Validates <paramref name="options"/>.</summary>
        /// <param name="name">Unused; this type has one instance.</param>
        /// <param name="options">The bound settings.</param>
        /// <returns>A successful result, or a failure carrying one message per broken rule.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.</exception>
        public ValidateOptionsResult Validate(string? name, PasswordPolicyOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);

            List<string> failures = [];
            string section = PasswordPolicyOptions.SectionName;

            // THE OPTIONS TYPE'S OWN RULES ARE APPLIED FIRST. Each options class carries a package-free
            // Validate stating the rules that belong to the contract itself, so that the Application
            // project can declare them without referencing the options package.
            failures.AddRange(options.Validate());

            if (options.MinRequiredPasswordLength < PasswordPolicyOptions.MinimumConfigurablePasswordLength)
            {
                failures.Add(
                    $"'{section}:{nameof(PasswordPolicyOptions.MinRequiredPasswordLength)}' is {options.MinRequiredPasswordLength}; it must be at least {PasswordPolicyOptions.MinimumConfigurablePasswordLength}, which is the measured legacy minimum. Lowering it would accept credentials the legacy installation rejected.");
            }

            if (options.MinRequiredPasswordLength > CredentialBounds.MaximumByteLength)
            {
                failures.Add(
                    $"'{section}:{nameof(PasswordPolicyOptions.MinRequiredPasswordLength)}' is {options.MinRequiredPasswordLength}, which exceeds the {CredentialBounds.MaximumByteLength}-byte ceiling every credential is bounded by. No password could satisfy the policy.");
            }

            if (options.MinRequiredNonAlphanumericCharacters < 0)
            {
                failures.Add(
                    $"'{section}:{nameof(PasswordPolicyOptions.MinRequiredNonAlphanumericCharacters)}' is {options.MinRequiredNonAlphanumericCharacters}; a count cannot be negative.");
            }
            else if (options.MinRequiredNonAlphanumericCharacters > options.MinRequiredPasswordLength)
            {
                failures.Add(
                    $"'{section}:{nameof(PasswordPolicyOptions.MinRequiredNonAlphanumericCharacters)}' is {options.MinRequiredNonAlphanumericCharacters}, which exceeds '{nameof(PasswordPolicyOptions.MinRequiredPasswordLength)}' of {options.MinRequiredPasswordLength}. No password could satisfy both.");
            }

            if (options.RequiresQuestionAndAnswer)
            {
                failures.Add(
                    $"'{section}:{nameof(PasswordPolicyOptions.RequiresQuestionAndAnswer)}' must be false. No endpoint in this application collects or verifies a security question, so a true value would be enforced nowhere while appearing to be a requirement.");
            }

            if (options.RequiresUniqueEmail)
            {
                failures.Add(
                    $"'{section}:{nameof(PasswordPolicyOptions.RequiresUniqueEmail)}' must be false. Nothing in this application checks an email address for uniqueness, so a true value would be enforced nowhere; the legacy installation permitted duplicates and the existing data may contain them.");
            }

            if (options.MaxInvalidPasswordAttempts <= 0)
            {
                failures.Add(
                    $"'{section}:{nameof(PasswordPolicyOptions.MaxInvalidPasswordAttempts)}' is {options.MaxInvalidPasswordAttempts}; zero or less would lock every account on its first mistyped password. The measured legacy value is 5.");
            }

            if (options.PasswordAttemptWindowMinutes <= 0)
            {
                failures.Add(
                    $"'{section}:{nameof(PasswordPolicyOptions.PasswordAttemptWindowMinutes)}' is {options.PasswordAttemptWindowMinutes}; a window has to be a period. The measured legacy value is 10.");
            }

            ValidateStrengthPattern(options.PasswordStrengthRegularExpression, section, failures);

            return failures.Count == 0
                ? ValidateOptionsResult.Success
                : ValidateOptionsResult.Fail(failures);
        }

        /// <summary>Checks the optional strength pattern.</summary>
        /// <param name="pattern">The configured pattern, possibly empty.</param>
        /// <param name="section">The section name, used in messages.</param>
        /// <param name="failures">Collects one message per broken rule.</param>
        private static void ValidateStrengthPattern(string pattern, string section, List<string> failures)
        {
            if (string.IsNullOrEmpty(pattern))
            {
                return;
            }

            if (pattern.Length > PasswordPolicyOptions.MaximumPasswordStrengthRegularExpressionLength)
            {
                failures.Add(
                    $"'{section}:{nameof(PasswordPolicyOptions.PasswordStrengthRegularExpression)}' is {pattern.Length} characters long; at most {PasswordPolicyOptions.MaximumPasswordStrengthRegularExpressionLength} are permitted.");
                return;
            }

            try
            {
                _ = new Regex(pattern, RegexOptions.None, TimeSpan.FromMilliseconds(100));
            }
            catch (ArgumentException exception)
            {
                failures.Add(
                    $"'{section}:{nameof(PasswordPolicyOptions.PasswordStrengthRegularExpression)}' is not a valid regular expression: {exception.Message}");
            }
        }
    }

    /// <summary>Checks the bound portal settings against the bounds <see cref="PortalOptions"/> declares.</summary>
    /// <remarks>
    /// Two of these rules are security controls rather than tidiness. Both configured path values are
    /// combined with a directory this application owns, so neither may be rooted or reach upward out of it;
    /// and the home-directory format must keep its per-portal placeholder, because a format that has lost
    /// it expands to the same directory for every tenant.
    /// </remarks>
    private sealed class PortalOptionsValidator : IValidateOptions<PortalOptions>
    {
        /// <summary>Validates <paramref name="options"/>.</summary>
        /// <param name="name">Unused; this type has one instance.</param>
        /// <param name="options">The bound settings.</param>
        /// <returns>A successful result, or a failure carrying one message per broken rule.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.</exception>
        public ValidateOptionsResult Validate(string? name, PortalOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);

            List<string> failures = [];
            string section = PortalOptions.SectionName;

            // THE OPTIONS TYPE'S OWN RULES ARE APPLIED FIRST. Each options class carries a package-free
            // Validate stating the rules that belong to the contract itself, so that the Application
            // project can declare them without referencing the options package.
            failures.AddRange(options.Validate());

            ValidatePathValue(
                nameof(PortalOptions.AdminTemplateFileName),
                options.AdminTemplateFileName,
                requireBareFileName: true,
                section,
                failures);

            ValidatePathValue(
                nameof(PortalOptions.HomeDirectoryFormat),
                options.HomeDirectoryFormat,
                requireBareFileName: false,
                section,
                failures);

            if (!string.IsNullOrWhiteSpace(options.HomeDirectoryFormat)
                && !options.HomeDirectoryFormat.Contains(PortalOptions.HomeDirectoryPortalPlaceholder, StringComparison.Ordinal))
            {
                failures.Add(
                    $"'{section}:{nameof(PortalOptions.HomeDirectoryFormat)}' does not contain the placeholder '{PortalOptions.HomeDirectoryPortalPlaceholder}'. Without it every portal would resolve to the same directory and tenants would share their files.");
            }

            return failures.Count == 0
                ? ValidateOptionsResult.Success
                : ValidateOptionsResult.Fail(failures);
        }

        /// <summary>Checks a value that becomes part of a file-system path.</summary>
        /// <param name="key">The setting's name, used in messages.</param>
        /// <param name="value">The configured value.</param>
        /// <param name="requireBareFileName">
        /// <see langword="true"/> when the value must be a single path segment with no separator at all.
        /// </param>
        /// <param name="section">The section name, used in messages.</param>
        /// <param name="failures">Collects one message per broken rule.</param>
        private static void ValidatePathValue(
            string key,
            string value,
            bool requireBareFileName,
            string section,
            List<string> failures)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                failures.Add($"'{section}:{key}' must not be blank.");
                return;
            }

            if (value.Length > PortalOptions.MaximumPathValueLength)
            {
                failures.Add(
                    $"'{section}:{key}' is {value.Length} characters long; at most {PortalOptions.MaximumPathValueLength} are permitted.");
            }

            if (value.Contains(PortalOptions.ParentDirectorySegment, StringComparison.Ordinal))
            {
                failures.Add(
                    $"'{section}:{key}' contains '{PortalOptions.ParentDirectorySegment}'. The value is combined with a directory this application owns, so it must not be able to reach outside it.");
            }

            if (Path.IsPathRooted(value) || value.StartsWith('/') || value.StartsWith('\\'))
            {
                failures.Add(
                    $"'{section}:{key}' is an absolute path. The value is combined with a directory this application owns, so it must be relative to it.");
            }

            if (requireBareFileName && (value.Contains('/', StringComparison.Ordinal) || value.Contains('\\', StringComparison.Ordinal)))
            {
                failures.Add(
                    $"'{section}:{key}' contains a directory separator; it must name a single file.");
            }
        }
    }

    /// <summary>Checks the bound caching settings against the bounds <see cref="CachingOptions"/> declares.</summary>
    private sealed class CachingOptionsValidator : IValidateOptions<CachingOptions>
    {
        /// <summary>Validates <paramref name="options"/>.</summary>
        /// <param name="name">Unused; this type has one instance.</param>
        /// <param name="options">The bound settings.</param>
        /// <returns>A successful result, or a failure describing the problem.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.</exception>
        public ValidateOptionsResult Validate(string? name, CachingOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);

            List<string> failures = [];

            // A defect both halves cover is reported twice, in each half's own wording.
            failures.AddRange(options.Validate());

            if (options.PerformanceMultiplier is < CachingOptions.MinimumPerformanceMultiplier
                or > CachingOptions.MaximumPerformanceMultiplier)
            {
                failures.Add(
                    $"'{CachingOptions.SectionName}:{nameof(CachingOptions.PerformanceMultiplier)}' is {options.PerformanceMultiplier}; it must be between {CachingOptions.MinimumPerformanceMultiplier} and {CachingOptions.MaximumPerformanceMultiplier} inclusive. Zero is permitted and disables caching.");
            }

            return failures.Count == 0
                ? ValidateOptionsResult.Success
                : ValidateOptionsResult.Fail(failures);
        }
    }

    /// <summary>Validates the refresh-token store settings while the host is starting.</summary>
    /// <remarks>
    /// The same two-part split the other validators here use.
    /// </remarks>
    private sealed class RefreshTokenStoreOptionsValidator : IValidateOptions<RefreshTokenStoreOptions>
    {
        /// <summary>Validates <paramref name="options"/>.</summary>
        /// <param name="name">Unused; this type has one instance.</param>
        /// <param name="options">The bound settings.</param>
        /// <returns>A successful result, or a failure describing every problem found.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.</exception>
        public ValidateOptionsResult Validate(string? name, RefreshTokenStoreOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);

            List<string> failures = [];

            failures.AddRange(options.Validate());

            if (options.MaximumTrackedTokens is < RefreshTokenStoreOptions.MinimumTrackedTokenCeiling
                or > RefreshTokenStoreOptions.MaximumTrackedTokenCeiling)
            {
                failures.Add(FormattableString.Invariant(
                    $"'{RefreshTokenStoreOptions.SectionName}:{nameof(RefreshTokenStoreOptions.MaximumTrackedTokens)}' is {options.MaximumTrackedTokens}; it must be between {RefreshTokenStoreOptions.MinimumTrackedTokenCeiling} and {RefreshTokenStoreOptions.MaximumTrackedTokenCeiling} inclusive. Below the floor a single active caller can evict its own refresh family; above the ceiling the process-local store's memory footprint is the problem to solve rather than its capacity, and the answer is a shared store registered behind IRefreshTokenStore."));
            }

            if (options.ConcurrentUseGraceSeconds > RefreshTokenStoreOptions.MaximumConcurrentUseGraceSeconds)
            {
                failures.Add(FormattableString.Invariant(
                    $"'{RefreshTokenStoreOptions.SectionName}:{nameof(RefreshTokenStoreOptions.ConcurrentUseGraceSeconds)}' is {options.ConcurrentUseGraceSeconds}; it must not exceed {RefreshTokenStoreOptions.MaximumConcurrentUseGraceSeconds}. The grace is the window in which a spent refresh token presented again by the same client fingerprint is forgiven rather than treated as theft, so a long window weakens replay detection. Zero is permitted and disables the grace."));
            }

            return failures.Count == 0
                ? ValidateOptionsResult.Success
                : ValidateOptionsResult.Fail(failures);
        }
    }

    /// <summary>Holds the data-protection key ring in process memory, so no key is written anywhere.</summary>
    /// <remarks>
    /// A lock rather than a concurrent collection, because the contract has two operations and one of them
    /// returns the whole set: a snapshot taken while another thread appends must not observe a torn list,
    /// and the key manager reads the ring far more often than it writes to it. Reads copy the list, so a
    /// caller enumerating the result cannot be disturbed by a later write.
    /// </remarks>
    private sealed class InMemoryKeyRingRepository : IXmlRepository
    {
        private readonly List<XElement> _elements = [];
        private readonly object _gate = new();

        /// <inheritdoc />
        public IReadOnlyCollection<XElement> GetAllElements()
        {
            lock (_gate)
            {
                return _elements.ToList();
            }
        }

        /// <inheritdoc />
        /// <remarks>
        /// The element is copied before being stored. The key manager hands over an element it may still
        /// hold a reference to, and a repository that kept the caller's instance would let a later
        /// modification of it change what this ring believes was stored.
        /// </remarks>
        public void StoreElement(XElement element, string friendlyName)
        {
            ArgumentNullException.ThrowIfNull(element);

            lock (_gate)
            {
                _elements.Add(new XElement(element));
            }
        }
    }
}
