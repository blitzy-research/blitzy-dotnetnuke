using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using DnnMigration.Api.Authorization;
using DnnMigration.Api.ErrorHandling;
using DnnMigration.Api.Filters;
using DnnMigration.Application.Abstractions;
using DnnMigration.Application.Dtos.Module;
using DnnMigration.Application.Options;
using DnnMigration.Application.Serialization;
using DnnMigration.Application.Validation;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.HttpsPolicy;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Options;

// Two different types are called JsonOptions on this framework - the controller formatters' and the
// minimal-API writer's - and both are configured in this file, against two serialisation surfaces that must
// agree. Microsoft.AspNetCore.Mvc is imported above, so the unqualified name would bind to the MVC one; the
// alias names the OTHER one explicitly so neither call site can be read as the wrong surface.
using JsonOptions = Microsoft.AspNetCore.Http.Json.JsonOptions;

namespace DnnMigration.Api.Extensions;

/// <summary>
/// Registers the services the API layer itself contributes, and binds and validates every
/// configuration section other than the JWT settings.
/// </summary>
/// <remarks>
/// <para>
/// This file and its pipeline counterpart are what keep <c>Program.cs</c> a composition root rather
/// than a registration dump.
/// </para>
/// <para>
/// <b>Every bound section is validated, and validation is scheduled for startup.</b> A settings
/// type declares the bounds its values must satisfy; the validators nested below enforce them.
/// Scheduling the check for startup is the part that matters: options validation otherwise runs
/// when a value is first read, which for a setting used on one code path could be long after an
/// orchestrator concluded the container had started successfully.
/// </para>
/// </remarks>
public static class ServiceCollectionExtensions
{
    /// <summary>Largest request body the API accepts: one mebibyte.</summary>
    /// <remarks>
    /// <para>
    /// The server's own default is thirty megabytes, which for a JSON administration API is thirty
    /// megabytes of attacker-chosen allocation on every unauthenticated endpoint. This API's
    /// largest legitimate request is a portal or user record of a few kilobytes, so a mebibyte is
    /// generous by three orders of magnitude and still bounds the work a caller can create before
    /// any validator has run.
    /// </para>
    /// <para>
    /// This is the outer half of a pair. It bounds the whole body; the ceiling on an individual
    /// credential field is applied by the request validators, because a body limit cannot make one
    /// field bounded - a caller can spend the entire allowance on a single password.
    /// </para>
    /// </remarks>
    public const long MaximumRequestBodyBytes = 1L * 1024L * 1024L;

    /// <summary>Worst-case bytes one document character occupies once JSON-encoded: six.</summary>
    /// <remarks>Taken from the serialiser's escaping rules rather than estimated.</remarks>
    private const long MaximumJsonBytesPerCharacter = 6L;

    /// <summary>
    /// Headroom, in bytes, for everything in an import body that is not the document: sixty-four
    /// kibibytes.
    /// </summary>
    /// <remarks>
    /// The four member names, their quotes and separators, the module identifier and the
    /// descriptive file name. Generous by three orders of magnitude against the few hundred bytes
    /// those actually occupy, because the cost of over-providing here is nothing and the cost of
    /// under-providing is a refusal a caller cannot diagnose.
    /// </remarks>
    private const long ImportEnvelopeAllowanceBytes = 64L * 1024L;

    /// <summary>
    /// Largest request body the module import action accepts, computed from the document ceiling
    /// the import contract publishes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Computed rather than written down, so the limit cannot drift away from the contract it
    /// bounds.
    /// </para>
    /// <para>
    /// The global limit above is deliberately left alone. The reverse proxy in front of the API
    /// bounds the body first and must therefore be at least this large; <c>docker/nginx.conf</c>
    /// sets its API location's <c>client_max_body_size</c> to exactly this value, because nginx's
    /// own default is one mebibyte and would otherwise be the binding limit of the whole path.
    /// </para>
    /// </remarks>
    public const long MaximumImportRequestBodyBytes =
        (ModuleImportRequest.ContentCharacterMaximum * MaximumJsonBytesPerCharacter)
        + ImportEnvelopeAllowanceBytes;

    /// <summary>
    /// Configuration key listing the addresses of reverse proxies whose forwarded headers may be
    /// trusted: <c>Proxy:KnownProxies</c>.
    /// </summary>
    public const string KnownProxiesSectionName = "Proxy:KnownProxies";

    /// <summary>
    /// Configuration key listing the networks, in prefix notation, whose forwarded headers may be
    /// trusted: <c>Proxy:KnownNetworks</c>.
    /// </summary>
    public const string KnownNetworksSectionName = "Proxy:KnownNetworks";


    /// <summary>
    /// Reports whether the deployment has named at least one reverse proxy whose forwarded headers
    /// may be trusted.
    /// </summary>
    /// <param name="configuration">The application's configuration.</param>
    /// <returns>
    /// <see langword="true"/> when <see cref="KnownProxiesSectionName"/> or
    /// <see cref="KnownNetworksSectionName"/> names anything.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="configuration"/> is <see langword="null"/>.
    /// </exception>
    /// <remarks>
    /// <para>
    /// The pipeline asks this before registering the forwarded-headers stage, so that the stage
    /// exists only where a deployment has declared what it trusts. That is a security decision
    /// rather than an optimisation: promoting a forwarded address supplied by an untrusted hop lets
    /// a caller name its own address, and therefore choose its own rate-limit partition.
    /// </para>
    /// <para>
    /// SEC: BOTH MEMBERS READ THE SAME NORMALISED LIST, THROUGH <see cref="TrustedEntries"/>, AND
    /// THEY USED NOT TO. This one filtered blank entries while <see cref="AddForwardedHeaders"/>
    /// parsed every entry it found and threw on a blank one, so a deployment template that left an
    /// entry as an empty string produced one of two opposite outcomes depending on which member
    /// looked: this one said "no trusted proxy configured" and the pipeline stage was never
    /// registered, while the registration refused to start the host at all.
    /// </para>
    /// </remarks>
    public static bool HasTrustedProxies(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        return TrustedEntries(configuration, KnownProxiesSectionName).Count != 0
            || TrustedEntries(configuration, KnownNetworksSectionName).Count != 0;
    }

    /// <summary>
    /// Reads one trusted-hop section into the normalised entries every consumer must agree on.
    /// </summary>
    /// <param name="configuration">The application's configuration.</param>
    /// <param name="sectionName">
    /// <see cref="KnownProxiesSectionName"/> or <see cref="KnownNetworksSectionName"/>.
    /// </param>
    /// <returns>
    /// The declared entries, trimmed, with every blank or whitespace-only entry removed.
    /// </returns>
    /// <remarks>
    /// <para>Trimming is part of the normalisation rather than a courtesy.</para>
    /// <para>
    /// A blank entry is DROPPED rather than refused, and the direction matters: a deployment
    /// template that ships an empty entry is a template that has not configured a trusted hop,
    /// which is the safe reading. A NON-blank entry that is not an address or a network is still
    /// refused loudly by the parsers below, because that is a value an operator meant and got
    /// wrong.
    /// </para>
    /// </remarks>
    private static IReadOnlyList<string> TrustedEntries(
        IConfiguration configuration,
        string sectionName) =>
        [.. (configuration.GetSection(sectionName).Get<string[]>() ?? [])
            .Where(entry => !string.IsNullOrWhiteSpace(entry))
            .Select(entry => entry.Trim())];


    /// <summary>
    /// Adds the API layer's own services and binds every configuration section it owns.
    /// </summary>
    /// <param name="services">The container being populated.</param>
    /// <param name="configuration">The application's configuration.</param>
    /// <returns>The same <paramref name="services"/> instance, so calls can be chained.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="services"/> or <paramref name="configuration"/> is <see langword="null"/>.
    /// </exception>
    /// <remarks>
    /// The order of the calls below is not arbitrary. The problem-details factory is registered
    /// before the controller services, because those services register the framework's own factory
    /// only if none is present; registering afterwards would leave the framework's factory in place
    /// and this application's error payload unused, with nothing to indicate it.
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

        return services;
    }

    /// <summary>
    /// Binds the password-policy, portal and caching sections and schedules their validation for
    /// startup.
    /// </summary>
    /// <param name="services">The container being populated.</param>
    /// <param name="configuration">The application's configuration.</param>
    /// <remarks>
    /// The password-policy section is bound here rather than alongside the JWT settings even though
    /// both are security configuration, because three request validators resolve it and none of
    /// them is reachable from the authentication scheme.
    /// </remarks>
    private static void AddValidatedOptions(IServiceCollection services, IConfiguration configuration)
    {
        // MIGRATION: these bindings are the whole of what replaced the legacy provider model.
        // Website/release.config declared fourteen `defaultProvider` attributes, each naming a swappable
        // implementation that was then resolved by reflection at run time.
        services
            .AddOptions<PasswordPolicyOptions>()
            .Bind(configuration.GetSection(PasswordPolicyOptions.SectionName))
            .ValidateOnStart();

        services.AddSingleton<IValidateOptions<PasswordPolicyOptions>, PasswordPolicyOptionsValidator>();

        // The legacy-credential migration window is deliberately not bound here: its section carries a
        // deployment-supplied decryption key, so the type holding it stays internal to Infrastructure,
        // beside the verifier that consumes it, rather than becoming a publicly reachable options type this
        // layer could hand to anything.

        // The three password validators take a bound PasswordPolicyOptions directly, not
        // IOptions<PasswordPolicyOptions>: the Application project references FluentValidation and nothing
        // else, so the options abstraction is not available to it and a validator cannot be written against
        // it. Projecting the bound value here is what makes those constructors satisfiable.
        services.AddSingleton(
            provider => provider.GetRequiredService<IOptions<PasswordPolicyOptions>>().Value);

        services
            .AddOptions<PortalOptions>()
            .Bind(configuration.GetSection(PortalOptions.SectionName))
            .ValidateOnStart();

        services.AddSingleton<IValidateOptions<PortalOptions>, PortalOptionsValidator>();

        // PermissionService takes a bound PortalOptions directly, for the same reason the password
        // validators do: the Application project references FluentValidation and nothing else, so IOptions
        // is not available to a constructor declared there. The projection below is what makes that
        // constructor satisfiable.
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
    }

    /// <summary>
    /// Registers the accessor for the current request and the projection of the authenticated
    /// caller's claims.
    /// </summary>
    /// <param name="services">The container being populated.</param>
    /// <remarks>
    /// <para>
    /// Both belong to this layer because both describe a request. MIGRATION: the legacy equivalents
    /// were ambient statics - <c>HttpContext.Current.Items("PortalSettings")</c> at
    /// <c>PortalController.vb</c> and <c>UserController.GetCurrentUserInfo</c> at
    /// <c>UserController.vb</c> - reachable from anywhere, mutable in place, and impossible to
    /// substitute in a test.
    /// </para>
    /// <para>
    /// The claims projection is scoped rather than singleton because it reads the principal of the
    /// request being handled; capturing one in a singleton would pin the first caller's identity
    /// for the lifetime of the process.
    /// </para>
    /// </remarks>
    private static void AddRequestContextAccessors(IServiceCollection services)
    {
        services.AddHttpContextAccessor();

        services.AddScoped<ICurrentUser, CurrentUser>();
    }

    /// <summary>
    /// Registers the problem-details service, this application's problem-details factory, and the
    /// handler that turns an escaped exception into a problem document.
    /// </summary>
    /// <param name="services">The container being populated.</param>
    /// <remarks>
    /// <para>
    /// The problem-details service is not optional decoration. Two pipeline stages - the tenant
    /// resolver and the rate limiter's refusal path - resolve it directly in order to write their
    /// own refusals, and neither can be activated without it.
    /// </para>
    /// </remarks>
    private static void AddErrorHandling(IServiceCollection services)
    {
        services.AddProblemDetails();

        services.AddSingleton<ProblemDetailsFactory, ValidationProblemDetailsFactory>();

        // The writer behind IProblemDetailsService serialises through the minimal-API options object, not
        // the controller formatters, so the enumeration policy has to be applied to both. Without this a
        // value carried in a problem-details extension member would be spelled one way in a successful
        // result and another in an error, and a client parsing one contract has no reason to expect that.
        services.Configure<JsonOptions>(options =>
        {
            DnnJsonConverters.AddTo(options.SerializerOptions);

            // Stated rather than inherited on both surfaces. Each already holds the value being assigned, so
            // neither changes behaviour today - which is why they are worth writing down: two halves of one
            // contract that agree only through a shared framework default can be separated by a change to
            // that default, silently.
            options.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.Never;
            options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        });

        services.AddExceptionHandler<GlobalExceptionHandler>();
    }

    /// <summary>Registers the controller services and the validation filter.</summary>
    /// <param name="services">The container being populated.</param>
    /// <remarks>
    /// <para>
    /// The validation filter is added globally rather than annotated on each action. A per-action
    /// annotation is a thing that can be forgotten, and forgetting it produces an endpoint whose
    /// declared validation rules are never consulted - which looks exactly like an endpoint that
    /// has none.
    /// </para>
    /// <para>
    /// The filter is added by type rather than as an instance, so it is activated per request from
    /// the container. It depends on the problem-details factory, and registering an instance would
    /// require constructing it here, before that factory exists.
    /// </para>
    /// </remarks>
    private static void AddControllerServices(IServiceCollection services)
    {
        services
            .AddControllers(options =>
            {
                options.Filters.Add<FluentValidationActionFilter>();

                // Labels every controller-produced problem document application/problem+json. Registered
                // globally rather than per controller because the guarantee is about the API's error surface
                // as a whole: one controller left out would emit the same document under a different media
                // type, which is the drift the filter exists to remove.
                //
                // The order is load-bearing: every controller carries [Produces("application/json")], itself
                // a result filter that clears and reassigns the result's content types, and at equal order a
                // controller-scoped filter runs AFTER a global one - so at the default order this filter's
                // work is overwritten on every response and the media type never changes.
                options.Filters.Add<ProblemDetailsContentTypeFilter>(order: 1);
            })
            .AddJsonOptions(options =>
            {
                // The application's own enumeration converters are added first, and the order is
                // load-bearing: a converter is selected by taking the first entry in this collection that
                // reports it can handle the type, so a general enumeration converter ahead of a specific one
                // would claim every enumeration and the specific one would never run. BillingFrequency is
                // why that matters - its members carry the legacy char(1) codes dbo.Roles.BillingFrequency
                // and dbo.Roles.TrialFrequency store, so the wire form must stay "M" rather than become
                // "Month".
                //
                // SortDirection is in that set for a different reason, and it is the reason a body-only
                // wire form is a hazard rather than a detail: PagedRequest.SortDir is bound from the QUERY
                // STRING on every collection endpoint and from a JSON BODY on POST /api/v1/users/search,
                // the compensating address an identifying account search uses so that personal data stays
                // out of the request target. The query binder resolves an enumeration through its type
                // converter and accepts the member NAME; System.Text.Json with no converter accepts only
                // the number. One member therefore had two incompatible spellings and every client
                // implemented the documented one, so the compensating search was answered 400 while the
                // query-string listing succeeded. The converter pins the body to the name the query string
                // already accepts.
                DnnJsonConverters.AddTo(options.JsonSerializerOptions);

                // No blanket enumeration converter is registered, and that is a decision: the remaining
                // enumerations on the wire - portal registration mode, banner advertising mode and module
                // visibility - travel as the integer discriminators their columns store, which is how the
                // Angular models consume them. Leaving it out also removes an ordering hazard, since a
                // blanket converter would have to be registered after every specific one to stay correct.

                // Every declared member is written, including one holding null: absence is expressed by the
                // member's VALUE being null, never by the member going missing. A policy that omitted
                // default-valued members would drop 0, "" and false too, and in this schema those are real
                // data - the legacy encoding used -1 and the empty string as absence markers while the
                // identity seeds make them legitimate keys (dbo.Portals.PortalID is IDENTITY(-1, 1);
                // dbo.Roles.RoleID, dbo.Tabs.TabID and dbo.Modules.ModuleID are IDENTITY(0, 1)).
                options.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.Never;

                // Member names are camel-cased, stated rather than inherited for the same reason as the
                // condition above: every model under frontend/src/app/core/models is spelled in camel case,
                // so a change here would rename every member of every response at once and no compiler on
                // either side would notice.
                options.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;

                // A member no contract declares is a refused request, not a discarded one. Silent discarding
                // is what lets a client and a request DTO disagree about a member name while both appear to
                // work - the module-settings screen and UpdateModuleRequest spell two of these differently,
                // one after the legacy object and one after the legacy screen - so an undeclared member is
                // rejected rather than dropped.
                options.JsonSerializerOptions.UnmappedMemberHandling =
                    JsonUnmappedMemberHandling.Disallow;
            });

        // The framework maps sixteen status codes to a problem-type link and a title, and 429 is not one of
        // them - it postdates the specification the defaults cite. Without an entry a refusal from the
        // credential rate limiter is published without a "type" member, so it is the single response in the
        // application whose shape differs from every other, which a client parsing one contract has no
        // reason to expect.
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
    /// <para>
    /// Applied to the web server's own limits rather than through a filter, so it is enforced while
    /// the body is still being read off the socket. A limit applied after model binding would be
    /// applied after the allocation it exists to prevent.
    /// </para>
    /// <para>
    /// This is the limit for EVERY endpoint, and it stays at one mebibyte. The single action that
    /// needs more raises it for itself with the per-action attribute, which the framework applies
    /// by replacing the request's own maximum before the body is read - so the larger allowance is
    /// reachable on that action and nowhere else.
    /// </para>
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
    /// Kestrel remains plain HTTP on the private container network; nginx terminates TLS and
    /// forwards the original scheme.
    /// <para>
    /// <b>ONE registration, ONE key.</b> This concern was configured twice for a while, from two
    /// key names: this stage read <c>Https:RedirectPort</c> and validated it at start-up, while a
    /// second registration read a <c>Https:Port</c> of its own and defaulted it. The duplicate is
    /// withdrawn and the key is <c>Https:RedirectPort</c>: this process never listens on TLS, so a
    /// bare "port" would wrongly read as a listener, whereas the port a redirect names is exactly
    /// what a deployment configures.
    /// </para>
    /// <para>
    /// <b>The status is 308, and the alternative was considered.</b> A permanent redirect is cached
    /// by the browser and by every intermediary between it and the deployment, so a host that later
    /// has to answer on plain HTTP - during a certificate replacement, or after moving TLS
    /// termination - stays unreachable for callers that cached it; the framework's own default of
    /// 307 avoids that. It is nonetheless 308 here deliberately: both codes preserve the method and
    /// the body, which is the property that matters, and a permanent answer is the honest one for a
    /// deployment whose whole perimeter terminates TLS.
    /// </para>
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
    /// Configures which reverse proxies, if any, are trusted to report the original client's
    /// address and scheme.
    /// </summary>
    /// <param name="services">The container being populated.</param>
    /// <param name="configuration">The application's configuration.</param>
    /// <exception cref="InvalidOperationException">
    /// A configured proxy address or network cannot be parsed.
    /// </exception>
    /// <remarks>
    /// <para>
    /// <b>Nothing is trusted unless a deployment names it.</b> With no configuration the
    /// framework's default trust - the loopback address only - is left in place, which in a
    /// container means forwarded headers are effectively ignored and the address this application
    /// observes is the proxy's own.
    /// </para>
    /// </remarks>
    private static void AddForwardedHeaders(IServiceCollection services, IConfiguration configuration)
    {
        // The SAME normalised reading HasTrustedProxies uses, so the two cannot disagree about whether a
        // section declares anything. See the remarks on TrustedEntries for why a blank entry is absent
        // rather than a fault, and why a malformed non-blank entry still stops the host.
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

            // Replaced rather than added to. The default trust list contains the loopback address and
            // network, which inside a container is this process itself; leaving them in place alongside an
            // explicit list would trust a hop the deployment did not name.
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
    /// The network's base address and prefix length, both already checked for consistency with each
    /// other.
    /// </returns>
    /// <exception cref="InvalidOperationException">
    /// The value is not a network in prefix notation, or its prefix length is not valid for the
    /// address family.
    /// </exception>
    /// <remarks>
    /// <para>A malformed entry stops the host rather than being skipped.</para>
    /// <para>
    /// The validated parts are returned rather than a constructed network, so that the caller
    /// constructs whichever network type the middleware's collection holds.
    /// </para>
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
    /// Checks the bound password policy against the bounds <see cref="PasswordPolicyOptions"/>
    /// declares.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every rule here is either a <em>floor</em> that stops the legacy policy being weakened, or a
    /// check that a setting is <em>satisfiable</em>. None of them tightens the policy: the
    /// migration preserves it verbatim precisely so that users the legacy installation accepted are
    /// not locked out at the moment they are migrated.
    /// </para>
    /// <para>
    /// Two flags are required to be off, and the reason is worth stating plainly: nothing in this
    /// solution enforces either of them.
    /// </para>
    /// </remarks>
    private sealed class PasswordPolicyOptionsValidator : IValidateOptions<PasswordPolicyOptions>
    {
        /// <summary>Validates <paramref name="options"/>.</summary>
        /// <param name="name">Unused; this type has one instance.</param>
        /// <param name="options">The bound settings.</param>
        /// <returns>
        /// A successful result, or a failure carrying one message per broken rule.
        /// </returns>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="options"/> is <see langword="null"/>.
        /// </exception>
        public ValidateOptionsResult Validate(string? name, PasswordPolicyOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);

            List<string> failures = [];
            string section = PasswordPolicyOptions.SectionName;

            // THE OPTIONS TYPE'S OWN RULES ARE APPLIED FIRST. Each options class carries a package-free
            // Validate stating the rules that belong to the contract itself, so that the Application project
            // can declare them without referencing the options package.
            //
            // A defect that both halves cover is reported twice, in each half's own wording. That is
            // deliberate: the report is a start-up abort listing every problem found, and naming the same
            // setting twice is not misleading, whereas dropping a rule to keep the list tidy is.
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
        /// <remarks>
        /// <para>
        /// An empty pattern is the shipped value and means "no strength rule"; it is not checked, because
        /// there is nothing to check.
        /// </para>
        /// <para>
        /// A non-empty pattern is compiled here so that an invalid one stops the host.
        /// </para>
        /// </remarks>
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

    /// <summary>
    /// Checks the bound portal settings against the bounds <see cref="PortalOptions"/> declares.
    /// </summary>
    /// <remarks>
    /// Two of these rules are security controls rather than tidiness. Both configured path values
    /// are combined with a directory this application owns, so neither may be rooted or reach
    /// upward out of it; and the home-directory format must keep its per-portal placeholder,
    /// because a format that has lost it expands to the same directory for every tenant.
    /// </remarks>
    private sealed class PortalOptionsValidator : IValidateOptions<PortalOptions>
    {
        /// <summary>Validates <paramref name="options"/>.</summary>
        /// <param name="name">Unused; this type has one instance.</param>
        /// <param name="options">The bound settings.</param>
        /// <returns>
        /// A successful result, or a failure carrying one message per broken rule.
        /// </returns>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="options"/> is <see langword="null"/>.
        /// </exception>
        public ValidateOptionsResult Validate(string? name, PortalOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);

            List<string> failures = [];
            string section = PortalOptions.SectionName;

            // THE OPTIONS TYPE'S OWN RULES ARE APPLIED FIRST. Each options class carries a package-free
            // Validate stating the rules that belong to the contract itself, so that the Application project
            // can declare them without referencing the options package.
            //
            // A defect that both halves cover is reported twice, in each half's own wording. That is
            // deliberate: the report is a start-up abort listing every problem found, and naming the same
            // setting twice is not misleading, whereas dropping a rule to keep the list tidy is.
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

            // MIGRATION: three role-name rules used to be enforced here - blank, over-long, and the two
            // names colliding. All three are gone because the settings are gone: the special role names are
            // now the immutable domain constants DnnMigration.Domain.Common.SpecialRoleNames.AllUsers and
            // .Unauthenticated, so no deployment value exists to validate and no deployment value can move
            // an authorization audience onto a different row of the Roles table.

            return failures.Count == 0
                ? ValidateOptionsResult.Success
                : ValidateOptionsResult.Fail(failures);
        }

        /// <summary>Checks a value that becomes part of a file-system path.</summary>
        /// <param name="key">The setting's name, used in messages.</param>
        /// <param name="value">The configured value.</param>
        /// <param name="requireBareFileName">
        /// <see langword="true"/> when the value must be a single path segment with no separator at
        /// all.
        /// </param>
        /// <param name="section">The section name, used in messages.</param>
        /// <param name="failures">Collects one message per broken rule.</param>
        /// <remarks>
        /// A parent-directory segment and a rooted value are both rejected outright rather than
        /// normalised.
        /// </remarks>
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

    /// <summary>
    /// Checks the bound caching settings against the bounds <see cref="CachingOptions"/> declares.
    /// </summary>
    /// <remarks>
    /// Zero is accepted, and that is the interesting part: it is the legacy-sanctioned way to
    /// disable caching, so the floor is zero rather than one. A negative value is refused because
    /// it yields a negative expiry, which no cache entry can carry.
    /// </remarks>
    private sealed class CachingOptionsValidator : IValidateOptions<CachingOptions>
    {
        /// <summary>Validates <paramref name="options"/>.</summary>
        /// <param name="name">Unused; this type has one instance.</param>
        /// <param name="options">The bound settings.</param>
        /// <returns>A successful result, or a failure describing the problem.</returns>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="options"/> is <see langword="null"/>.
        /// </exception>
        public ValidateOptionsResult Validate(string? name, CachingOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);

            List<string> failures = [];

            // The contract's own Validate is the canonical statement of the rules the options type can judge
            // alone - here, that the multiplier is not negative. This validator is what WIRES it into the
            // host's ValidateOnStart and then adds the rule only a host can decide: the upper bound.
            //
            // A defect both halves cover is reported twice, in each half's own wording. That is deliberate,
            // for the reason given on the password-policy validator above: the report is a start-up abort
            // listing every problem found, and naming one setting twice is not misleading, whereas dropping
            // a rule to keep the list tidy is.
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
}
